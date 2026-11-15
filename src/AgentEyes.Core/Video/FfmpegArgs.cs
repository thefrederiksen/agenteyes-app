using System;
using System.Collections.Generic;
using Drawing = System.Drawing;

namespace AgentEyes.Video
{
    /// <summary>
    /// Pure ffmpeg argument builders. Kept side-effect free so they can be unit tested
    /// without launching ffmpeg.
    /// </summary>
    internal static partial class FfmpegArgs
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
            var (grab, padFilter) = GrabAndPad(capture, desktop);

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

        /// <summary>
        /// Split a requested capture region into what gdigrab can actually grab (the part inside the
        /// desktop) and the pad filter that composes it back to the exact requested size (issue #69),
        /// or no filter when the region fits. Shared by the normal recorder and always-on (issue #66).
        /// </summary>
        internal static (Drawing.Rectangle Grab, string? PadFilter) GrabAndPad(
            Drawing.Rectangle capture, Drawing.Rectangle? desktop)
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
            return (grab, padFilter);
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

        /// <summary>
        /// Stream a DirectShow camera as RAW BGR24 frames of a FIXED size on stdout (issue #29) -
        /// the preset editor's live preview.
        ///
        /// Raw frames rather than an encoded stream, because the preview's whole job is to prove the
        /// camera is pointed at the right thing: every frame is exactly
        /// <c>width * height * 3</c> bytes, so the reader knows a frame is complete by COUNTING
        /// rather than by parsing a container, and a WPF WriteableBitmap takes the buffer as-is.
        ///
        /// The frame rate is limited on the OUTPUT (<c>-r</c>), not requested from the device
        /// (<c>-framerate</c>). A dshow input REFUSES to open when the camera does not offer the
        /// requested mode - which would turn a perfectly good camera into a preview that will not
        /// start - so the device is opened at whatever it does natively and the surplus frames are
        /// dropped on the way out (issue #29, assumption B2: this is a framing check, not a monitor).
        ///
        /// The scale is aspect-preserving and padded to the exact requested box, so a 16:9 camera
        /// shows letterboxed rather than stretched, and the frame size stays constant whatever the
        /// camera's native resolution is.
        /// </summary>
        /// <param name="dshowCameraName">Exact DirectShow device name (from FfmpegDevices.ListVideo).</param>
        /// <param name="width">Frame width in pixels (even).</param>
        /// <param name="height">Frame height in pixels (even).</param>
        /// <param name="fps">Frames per second delivered on stdout.</param>
        public static List<string> CameraPreview(string dshowCameraName, int width, int height, int fps)
        {
            if (string.IsNullOrWhiteSpace(dshowCameraName))
                throw new UsageException("a camera preview needs an exact DirectShow device name.");
            if (width < 2 || height < 2 || width % 2 != 0 || height % 2 != 0)
                throw new UsageException(
                    $"a camera preview frame must be at least 2x2 and even-sized; got {width}x{height}.");
            if (fps < 1)
                throw new UsageException($"a camera preview needs a positive frame rate; got {fps}.");

            string fit = $"scale={width}:{height}:force_original_aspect_ratio=decrease,"
                       + $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black";

            return new List<string>
            {
                "-f", "dshow",
                // The same packet-drop guard the recording inputs carry.
                "-thread_queue_size", "512",
                "-i", $"video={dshowCameraName}",
                // The preview never carries audio: it must not open, and must not be able to open,
                // the camera's microphone.
                "-an",
                "-vf", fit,
                "-r", fps.ToString(),
                "-pix_fmt", "bgr24",
                "-f", "rawvideo",
                // Push each frame out as it is produced instead of leaving the tail of it in the
                // 32 KB IO buffer until the next one arrives.
                "-flush_packets", "1",
                "pipe:1",
            };
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
                denoise = $"{ArnndnFraming},arnndn=m='{FilterPath(o.RnnoiseModelPath!)}',";
            }
            // The threshold is MEASURED from the capture, never assumed. Building the chain with
            // the gate on but nothing measured is a programming error and says so, exactly like the
            // missing RNNoise model above - it must not quietly become some default number, because
            // an assumed threshold is the defect this replaced.
            string gate = "";
            if (o.NoiseGate)
            {
                if (!o.GateCalibrated)
                    throw new UsageException(
                        "the noise gate is on but the capture has not been measured - " +
                        "call AudioMix calibration before building mix args");

                // A measurement that found no room for a gate leaves the stage out entirely.
                if (o.GateThresholdLinear != null)
                    gate = $"agate=threshold={Inv(o.GateThresholdLinear.Value)}:ratio=2:attack=20:release=250,";
            }
            // e=4: boost quiet speech up to 4x (+12 dB); p=0.95: normalize peaks to 95%.
            string level = o.VoiceLeveling ? "speechnorm=e=4:p=0.95," : "";

            // Issue #128: replace any non-finite sample with silence BEFORE the first stage that
            // cannot survive one. A single NaN sample makes speechnorm spin at 100% CPU forever
            // (measured on ffmpeg 8.1.1 and 9.0.2, Windows and macOS), and a single NaN into arnndn
            // poisons its recurrent state so every sample after it comes out NaN - which then hangs
            // speechnorm. NaN/Inf is not audio; zeroing that one sample is inaudible. It is only
            // emitted when one of those two stages is in the chain.
            string finite = (o.NoiseSuppression || o.VoiceLeveling) ? FiniteSamples + "," + PinMicLayout(o) + "," : "";
            return $"[0:a]{finite}{denoise}{gate}{level}volume={Inv(o.MicGain)}[m]";
        }

        /// <summary>
        /// Issue #128: per-sample, per-channel (<c>ch</c> = the channel the expression runs for;
        /// <c>c=same</c> keeps the input layout) replacement of NaN and +/-Inf with 0. Finite
        /// samples pass through unchanged.
        /// </summary>
        internal const string FiniteSamples = "aeval=exprs='if(isnan(val(ch))+isinf(val(ch)),0,val(ch))':c=same";

        /// <summary>
        /// Issue #128 (round two, D3): hand arnndn only whole 480-sample frames. arnndn works in
        /// 480-sample frames and always rounds its output up to a whole frame; when the input ends
        /// part-way through one, ffmpeg 9.0.2 (gyan.dev Windows build) emits that final frame as 480
        /// NaN samples (measured: mono and stereo, s16 and float; 8.1.1 emits finite samples), and
        /// speechnorm then spins forever on it. p=1 pads the last frame with digital silence, so
        /// arnndn never sees a partial frame. The output length is unchanged - arnndn already rounds
        /// up to the same whole frame - and every sample before the final frame is bit-identical
        /// (measured on 8.1.1 and 9.0.2). This AVOIDS the NaN rather than masking it: nothing after
        /// arnndn zeroes samples, so if arnndn ever emits a NaN for some other reason, speechnorm
        /// still stalls and the watchdog still fails the stage loudly instead of shipping silence.
        /// </summary>
        internal const string ArnndnFraming = "asetnsamples=n=480:p=1";

        /// <summary>
        /// Issue #128 round two (D7): pin the sanitizer's OUTPUT to the mic's own channel layout.
        /// aeval's c=same does not hold its output to its input during format negotiation: in a MIXED
        /// recording with a MONO mic and a STEREO system track, the downstream amix negotiates the mic
        /// branch to stereo, aeval is configured mono-in/stereo-out, and ffmpeg segfaults (exit 139,
        /// 8 of 10 runs on 9.0.2, also on 8.1.1). Measured with the pin: 10 of 10 complete, and the
        /// output is bit-identical to the chain without a sanitizer. Mic-only recordings (mono or
        /// stereo) and every other layout pair never crashed; the pin is harmless there.
        /// </summary>
        private static string PinMicLayout(AudioMixOptions o)
        {
            if (o.MicChannels is not int n || n <= 0)
                throw new UsageException(
                    "the mic's channel count has not been measured - call AudioMix preparation before " +
                    "building mix args (the NaN sanitizer is pinned to the mic's own layout)");
            string layout = n switch { 1 => "mono", 2 => "stereo", _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "c" };
            return "aformat=channel_layouts=" + layout;
        }

        /// <summary>Safety limiter appended after the last mix stage: stops clipping when the
        /// leveled mic and the system audio sum above full scale. level=false = no auto makeup gain.</summary>
        private const string Limiter = "alimiter=limit=0.95:level=false";

        /// <summary>Escape a Windows path for use inside an ffmpeg filtergraph option value
        /// (forward slashes; ':' is the filter option separator so it needs escaping).</summary>
        private static string FilterPath(string path) => path.Replace('\\', '/').Replace(":", "\\:");

        /// <summary>
        /// The clean-voice chain on the mic alone, ending at label [a]: the mic chain plus the
        /// safety limiter. One graph string, used by every builder that processes a microphone, so
        /// the processing cannot drift between the WAV path and the video path.
        /// </summary>
        /// <param name="micDelay">
        /// The mic's alignment stage, or "" (issue #114). See <see cref="MicDelayStage"/>: it is a
        /// filter plus its trailing comma, so an empty string emits the graph these builders emitted
        /// before alignment existed, byte for byte.
        /// </param>
        private static string MicOnlyGraph(AudioMixOptions o, string micDelay = "") =>
            $"{MicChain(o)};[m]{micDelay}{Limiter}[a]";

        /// <summary>
        /// The clean-voice chain on the mic (input 0) mixed with a system-audio track (input 1),
        /// ending at label [a]. normalize=0 keeps each source at the gain the preset chose instead
        /// of halving both.
        /// </summary>
        /// <param name="micDelay">The mic's alignment stage, or "" - see <see cref="MicOnlyGraph"/>.
        /// A delayed mic needs a stage of its own between [m] and the amix, so it gets one ([md]);
        /// with no delay the graph is exactly the one this builder emitted before.</param>
        private static string MicPlusSystemGraph(AudioMixOptions o, string micDelay = "")
        {
            string mic = micDelay.Length == 0 ? "[m]" : $"[m]{micDelay}anull[md];[md]";
            return $"{MicChain(o)};[1:a]volume={Inv(o.SystemGain)}[s];"
                 + $"{mic}[s]amix=inputs=2:duration=longest:normalize=0,{Limiter}[a]";
        }

        /// <summary>
        /// HOW THE TWO macOS CAPTURES ARE LINED UP (issue #114), as a filter stage for the mic
        /// branch of a pass-1 graph - or "" when there is nothing to do.
        ///
        /// On macOS the microphone is captured by its own ffmpeg, so the two captures are different
        /// processes with different device warm-ups and their t=0 are not the same instant.
        /// <paramref name="micOffsetSeconds"/> is how far the mic's first sample sits AFTER the first
        /// video frame, measured from the capture clock both ffmpegs report, and the sign convention
        /// is ComposeArgs.CameraInset's:
        ///  * positive - the mic started LATE, so its samples belong later in the timeline: pad the
        ///    head with that much silence (adelay, this stage) rather than letting speech run early;
        ///  * negative - the mic was already running, so the WAV holds audio from before the picture
        ///    existed: that head is seeked off the INPUT instead (see <see cref="MicInputSeek"/>),
        ///    which is the cheap form, and this stage is empty.
        /// Zero does neither, and every non-macOS caller passes zero.
        ///
        /// adelay wants milliseconds, and all=1 applies the one value to every channel. A
        /// sub-millisecond offset is rounded to what the filter can express.
        /// </summary>
        private static string MicDelayStage(double micOffsetSeconds) =>
            micOffsetSeconds > 0
                ? $"adelay={Inv(Math.Round(micOffsetSeconds * 1000.0, 3))}:all=1,"
                : "";

        /// <summary>
        /// The other half of <see cref="MicDelayStage"/>: a NEGATIVE offset means the mic WAV holds
        /// audio from before the first video frame, so that head is seeked off the input. Returns the
        /// "-ss &lt;seconds&gt;" pair to place immediately before the mic's own "-i", or an empty
        /// list. An INPUT seek, so it belongs before that -i and before nothing else.
        /// </summary>
        private static List<string> MicInputSeek(double micOffsetSeconds)
        {
            double skip = -micOffsetSeconds;
            return skip > 0
                ? new List<string> { "-ss", Inv(Math.Round(skip, 6)) }
                : new List<string>();
        }

        /// <summary>
        /// The sample format the intermediate processed-audio WAV is written in (issue #136). 32-bit
        /// float is what the filtergraph computes in and what the AAC encoder takes, so splitting the
        /// mux into two passes hands the encoder the SAME samples the single pass did - the two-pass
        /// output is not a quality step down from the one-pass output it replaces.
        /// </summary>
        internal const string ProcessedAudioCodec = "pcm_f32le";


        /// <summary>
        /// Mix a mic WAV and a system WAV into one WAV (48 kHz stereo): gate+volume the mic,
        /// volume the system, amix. normalize=0 keeps levels predictable.
        /// </summary>
        public static List<string> MixTwoWav(string micWav, string sysWav, string outWav, AudioMixOptions o)
        {
            return new()
            {
                "-y", "-i", micWav, "-i", sysWav,
                "-filter_complex", MicPlusSystemGraph(o), "-map", "[a]", "-ar", "48000", "-ac", "2", outWav,
            };
        }

        // ---- the two-pass video mux (issue #136) ---------------------------
        //
        // WHY THERE ARE TWO PASSES AND NOT ONE. Every one of these builders used to be a single
        // ffmpeg run that paired "-c:v copy" with a filtered audio stream, and on macOS that shape
        // DEADLOCKS: the copied video is read and queued almost instantly while the mic is still
        // being filtered, ffmpeg's muxing queue hits its default bound, and the process then spins
        // at ~99% CPU past the end of the content without ever finalizing. Measured on a 76 s
        // recording (issue #136): the shipped single-pass mux never completed, while the same filter
        // graph with no video present completed in about a second, and so did the same video copy
        // with no filter graph present. It needs BOTH halves in one command, which is why neither
        // half alone ever showed it.
        //
        // So the two halves are run as two commands:
        //   pass 1 - the audio filter graph alone, to an intermediate float WAV. No video stream in
        //            the command, so there is nothing to race the filters;
        //   pass 2 - the video copy plus that finished WAV. NO filter graph at all, so there is
        //            nothing for the video to race.
        // This is the architecture the system-audio path already used with sys_native.wav, and it
        // needs no tuned queue bound: "-max_muxing_queue_size <n>" also completed, but the depth
        // needed grows with how far the video can race ahead, so a number that is big enough for a
        // 76 s recording says nothing about a 10-minute one.
        //
        // The invariant this leaves behind: NO argument list in this file pairs a copied video
        // stream with a filter graph. FfmpegMuxShapeTests sweeps every builder here and fails if one
        // does - including a builder added later.
        //
        // ONE BOUND THE INTERMEDIATE DOES HAVE, named rather than left to be discovered. A plain
        // RIFF WAV holds its size in 32 bits and so cannot describe more than 4 GB, and float at
        // 48 kHz is 192 kB/s per channel - about 6 hours of mono or 3 hours of stereo. Long for a
        // recording, but not impossible, and being wrong about it silently is the same class of
        // defect as the tuned queue bound above. So pass 1 passes "-rf64 auto": a plain RIFF header
        // below 4 GB (measured: the same bytes apart from the 36-byte placeholder the upgrade needs)
        // and an RF64 header only once the file really grows past it. ffmpeg reads either, so pass 2
        // does not care which it got - also measured.

        /// <summary>
        /// PASS 1, microphone only: run the mic through the clean-voice chain and the limiter into an
        /// intermediate WAV. No video stream is present, which is the point (see above).
        /// </summary>
        /// <param name="micSource">The microphone capture - a WAV, or a video file whose first audio
        /// stream is the mic track.</param>
        /// <param name="micOffsetSeconds">How far the mic's first sample sits after the first video
        /// frame (issue #114, macOS - see <see cref="MicDelayStage"/>). Zero, which is what every
        /// platform whose screen recorder captures the mic itself passes, emits the argument list
        /// this builder emitted before alignment existed, byte for byte.</param>
        public static List<string> FilterMicToWav(
            string micSource, string outWav, AudioMixOptions o, double micOffsetSeconds = 0.0)
        {
            var a = new List<string> { "-y" };
            a.AddRange(MicInputSeek(micOffsetSeconds));
            a.AddRange(new[]
            {
                "-i", micSource,
                "-filter_complex", MicOnlyGraph(o, MicDelayStage(micOffsetSeconds)),
                "-map", "[a]", "-c:a", ProcessedAudioCodec,
                // "-rf64 auto": a plain RIFF WAV cannot describe more than 4 GB. See the block comment above.
                "-rf64", "auto", outWav,
            });
            return a;
        }

        /// <summary>
        /// PASS 1, microphone plus system audio: the clean-voice chain on the mic, the system gain on
        /// the system WAV, the two mixed and limited, into an intermediate WAV.
        /// </summary>
        /// <param name="micSource">The microphone capture - a WAV, or a video file whose first audio
        /// stream is the mic track.</param>
        /// <param name="micOffsetSeconds">The mic's alignment against the picture (issue #114,
        /// macOS - see <see cref="MicDelayStage"/>). It applies to the MIC and never to the system
        /// WAV: the system track is written by the same process as the picture. Zero emits the list
        /// this builder emitted before alignment existed, byte for byte.</param>
        public static List<string> MixMicSystemToWav(
            string micSource, string sysWav, string outWav, AudioMixOptions o,
            double micOffsetSeconds = 0.0)
        {
            var a = new List<string> { "-y" };
            a.AddRange(MicInputSeek(micOffsetSeconds));
            a.AddRange(new[]
            {
                "-i", micSource, "-i", sysWav,
                "-filter_complex", MicPlusSystemGraph(o, MicDelayStage(micOffsetSeconds)),
                "-map", "[a]", "-c:a", ProcessedAudioCodec,
                // "-rf64 auto": a plain RIFF WAV cannot describe more than 4 GB. See the block comment above.
                "-rf64", "auto", outWav,
            });
            return a;
        }

        /// <summary>
        /// PASS 1, system audio only: apply the system gain into an intermediate WAV. The filter is
        /// cheap enough that it has never been measured losing the race - it is split anyway so that
        /// the no-filter-graph-beside-a-copied-video invariant holds without an exemption, and so
        /// that a future stage added to this graph cannot reintroduce the deadlock.
        /// </summary>
        public static List<string> FilterSystemToWav(string sysWav, string outWav, double sysGain) => new()
        {
            "-y", "-i", sysWav,
            "-filter_complex", $"[0:a]volume={Inv(sysGain)}[a]",
            "-map", "[a]", "-c:a", ProcessedAudioCodec,
            // "-rf64 auto": a plain RIFF WAV cannot describe more than 4 GB. See the block comment above.
            "-rf64", "auto", outWav,
        };

        /// <summary>
        /// PASS 2: copy the video stream from <paramref name="rawMp4"/>, encode the ALREADY-PROCESSED
        /// audio from <paramref name="processedWav"/>, and mux them. There is deliberately no
        /// -filter_complex here: the audio arrives finished, so the only work beside the stream copy
        /// is the AAC encode, and the muxing queue has nothing to deadlock over.
        ///
        /// "-f mp4" is explicit rather than inferred from the output's extension, because this pass
        /// writes to a TEMP path that is renamed into place afterwards
        /// (<c>AudioMix.MuxingTempPath</c>, issue #136) and ffmpeg guesses the container from the
        /// file name: without it, ffmpeg refused the temp with "Unable to choose an output format
        /// ...; use a standard extension for the filename or specify the format manually" and exited
        /// 234. Measured, by the test that now covers the rename. Naming the format also takes the
        /// guess out of the normal case, where it happened to guess right.
        /// </summary>
        public static List<string> MuxVideoWithProcessedAudio(
            string rawMp4, string processedWav, string outMp4) => new()
        {
            "-y", "-i", rawMp4, "-i", processedWav,
            "-map", "0:v", "-c:v", "copy", "-map", "1:a", "-c:a", "aac", "-b:a", "160k",
            "-f", "mp4", outMp4,
        };

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
