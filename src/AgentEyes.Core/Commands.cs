using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AgentEyes.Audio;
using AgentEyes.Video;
using Drawing = System.Drawing;

namespace AgentEyes
{
    /// <summary>Implements each CLI command.</summary>
    internal static class Commands
    {
        /// <summary>Shared stop flag (a ref local can't be captured by the key-reader lambda).</summary>
        private sealed class Flag { public volatile bool Value; }

        // ---- screens -------------------------------------------------------

        public static int Screens()
        {
            Console.WriteLine("MONITORS (EnumDisplayMonitors)");
            Console.WriteLine("  #  Resolution      Position        Primary  Name");
            foreach (var m in Monitors.All())
            {
                Console.WriteLine(
                    $"  {m.Index}  {m.Width,5} x {m.Height,-5}  ({m.X}, {m.Y})".PadRight(34)
                    + $"  {(m.Primary ? "yes" : "no"),-7}  {m.Name}");
            }

            Console.WriteLine();
            Console.WriteLine("MICROPHONES - NAudio (used by 'audio' mode)");
            var devices = AudioCapture.Devices();
            if (devices.Length == 0) Console.WriteLine("  (none found)");
            foreach (var (number, name) in devices) Console.WriteLine($"  [{number}] {name}");

            Console.WriteLine();
            Console.WriteLine("MICROPHONES - DirectShow (used by 'video' mode)");
            try
            {
                var dshow = FfmpegDevices.ListAudio();
                if (dshow.Count == 0) Console.WriteLine("  (none found)");
                foreach (var name in dshow) Console.WriteLine($"  \"{name}\"");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  (unavailable: " + ex.Message + ")");
            }

            // Issue #28: the cameras 'video --camera' can record to camera.mp4. Same exact names the
            // Control API reports on GET /devices, from the same enumerator.
            Console.WriteLine();
            Console.WriteLine("CAMERAS: DirectShow video devices (used by 'video' mode --camera)");
            try
            {
                var cams = FfmpegDevices.ListVideo();
                if (cams.Count == 0) Console.WriteLine("  (none found)");
                foreach (var name in cams) Console.WriteLine($"  \"{name}\"");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  (unavailable: " + ex.Message + ")");
            }
            return 0;
        }

        // ---- shot (Mode C) -------------------------------------------------

        public static int Shot(CliArgs opts)
        {
            int screen = opts.RequireInt("screen", "e.g. agenteyes shot --screen 2 [--region]");
            var monitor = Monitors.Require(screen);
            string dir = NewSessionDir(opts, "shot");

            string file;
            var manifest = NewManifest("shot", opts, monitor);

            if (opts.Has("region"))
            {
                Console.WriteLine("[overlay] drag a rectangle ... release to capture (Esc to cancel)");
                var rect = RegionOverlay.Select();
                if (rect == null)
                {
                    Console.WriteLine("[cancelled] no region selected.");
                    return 1;
                }
                file = Path.Combine(dir, "shots",
                    $"region_{rect.Value.X}x{rect.Value.Y}_{rect.Value.Width}x{rect.Value.Height}.png");
                Screenshot.CaptureRect(rect.Value, file, copyToClipboard: true);
                manifest.Region = new[] { rect.Value.X, rect.Value.Y, rect.Value.Width, rect.Value.Height };
            }
            else
            {
                file = Path.Combine(dir, "shots", $"monitor{monitor.Index}_full.png");
                Screenshot.CaptureMonitor(monitor, file, copyToClipboard: true);
            }

            manifest.Files.Add(Path.GetFileName(file));
            // Issue #155: the CLI session owns this directory outright - it created it in this run -
            // so the whole record is this object. Atomic all the same.
            ManifestStore.Replace(dir, manifest);

            Console.WriteLine($"[ok] saved  {file}");
            Console.WriteLine("[ok] copied to clipboard");
            return 0;
        }

        // ---- audio (Mode A) ------------------------------------------------

        public static int Audio(CliArgs opts)
        {
            int screen = opts.RequireInt("screen", "e.g. agenteyes audio --screen 2 --mic \"Yeti\"");
            var monitor = Monitors.Require(screen);
            bool loopback = opts.Has("loopback");
            bool mix = opts.Has("mix");

            string dir = NewSessionDir(opts, "audio");
            string wav = Path.Combine(dir, "audio.wav");

            var manifest = NewManifest("audio", opts, monitor);
            manifest.AudioFile = "audio.wav";

            // Issue #83: the untouched pre-processing captures are preserved (renamed to ".original")
            // rather than deleted; collected here and recorded in the manifest below.
            var originals = new System.Collections.Generic.List<string>();

            var sw = new Stopwatch();
            var flag = new Flag();
            float peak = 0f;
            int autoStop = ParseAutoStop(opts);

            if (mix)
            {
                string micName = opts.Require("mic", "mixed needs a mic: --mix --mic \"Yeti\"");
                int device = AudioCapture.ResolveDevice(micName);
                var mixOpts = MixOpts(opts, AudioSourceKind.Mixed);
                string micWav = Path.Combine(dir, "mic.wav");
                string sysNative = Path.Combine(dir, "sys_native.wav");

                using var micCap = new AudioCapture(device);
                using var sysCap = new Audio.LoopbackCapture();
                float pMic = 0f, pSys = 0f;
                micCap.LevelChanged += p => pMic = p;
                sysCap.LevelChanged += p => pSys = p;
                manifest.Microphone = $"{AudioCapture.Devices()[device].Name} + (system)";

                Console.WriteLine($"[ok] recording MIC + SYSTEM (mixed, {FxDesc(mixOpts)})  -> {dir}");
                Console.WriteLine("     hotkeys: S=screenshot  Q=stop");

                var keys = new Thread(() => SessionKeys(flag, sw, monitor, dir, manifest)) { IsBackground = true };
                sw.Start();
                micCap.Start(micWav);
                sysCap.Start(sysNative);
                keys.Start();
                RunMeterLoop(flag, () => Math.Max(pMic, pSys), sw, autoStop);
                micCap.Stop();
                sysCap.Stop();
                sw.Stop();
                Console.WriteLine();

                AgentEyes.Audio.AudioMix.MixWavs(micWav, sysNative, wav, mixOpts);
                originals.AddRange(OriginalBackup.Preserve(dir, "audio", AudioSourceKind.Mixed));
            }
            else if (loopback)
            {
                string nativeWav = Path.Combine(dir, "audio_native.wav");
                using var cap = new Audio.LoopbackCapture();
                cap.LevelChanged += p => peak = p;
                manifest.Microphone = "(system loopback)";

                Console.WriteLine($"[ok] recording SYSTEM AUDIO (WASAPI loopback)  -> {dir}");
                Console.WriteLine("     hotkeys: S=screenshot  Q=stop");

                var keys = new Thread(() => SessionKeys(flag, sw, monitor, dir, manifest)) { IsBackground = true };
                sw.Start();
                cap.Start(nativeWav);
                keys.Start();
                RunMeterLoop(flag, () => peak, sw, autoStop);
                cap.Stop();
                sw.Stop();
                Console.WriteLine();

                // Downmix the native mix format to 16 kHz mono for Whisper.
                Ffmpeg.Run(FfmpegArgs.ExtractWav(nativeWav, wav), "downmix loopback");
                // Issue #83: keep the untouched native loopback capture instead of deleting it.
                if (File.Exists(nativeWav))
                {
                    File.Move(nativeWav, Path.Combine(dir, "audio.original.wav"), overwrite: true);
                    originals.Add("audio.original.wav");
                }
            }
            else
            {
                string mic = opts.Require("mic", "e.g. --mic \"Yeti\" (or use --loopback). 'agenteyes screens' lists names.");
                int device = AudioCapture.ResolveDevice(mic);
                using var audio = new AudioCapture(device);
                audio.LevelChanged += p => peak = p;
                manifest.Microphone = AudioCapture.Devices()[device].Name;

                Console.WriteLine($"[ok] recording mic [{device}] {manifest.Microphone}  -> {dir}");
                Console.WriteLine("     hotkeys: S=screenshot  P=pause/resume  Q=stop");

                var keys = new Thread(() => SessionKeys(flag, sw, monitor, dir, manifest)) { IsBackground = true };
                sw.Start();
                audio.Start(wav);
                keys.Start();
                RunMeterLoop(flag, () => peak, sw, autoStop);
                audio.Stop();
                sw.Stop();
                Console.WriteLine();
            }

            manifest.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
            manifest.Files.Add("audio.wav");
            foreach (var s in manifest.Shots) manifest.Files.Add(s.File);
            foreach (var o in originals) { manifest.OriginalFiles.Add(o); manifest.Files.Add(o); }
            ManifestStore.Replace(dir, manifest);

            Console.WriteLine($"[ok] audio.wav ({Timecodes.Label(sw.Elapsed)}), {manifest.Shots.Count} screenshot(s)");
            Console.WriteLine($"[ok] manifest.json written to {dir}");
            return 0;
        }

        // ---- video (Mode B) ------------------------------------------------

        public static int Video(CliArgs opts)
        {
            int screen = opts.RequireInt("screen", "e.g. agenteyes video --screen 2 --mic \"Yeti\"");
            var monitor = Monitors.Require(screen);

            // Capture target: full monitor, or a region.
            Drawing.Rectangle capture = monitor.Bounds;
            int[]? regionField = null;
            if (opts.Has("region"))
            {
                Console.WriteLine("[overlay] drag a rectangle ... release to start (Esc to cancel)");
                var rect = RegionOverlay.Select();
                if (rect == null) { Console.WriteLine("[cancelled] no region selected."); return 1; }
                capture = rect.Value;
                regionField = new[] { rect.Value.X, rect.Value.Y, rect.Value.Width, rect.Value.Height };
            }

            // Mic via DirectShow (video addresses mics by name). --mic optional.
            string? dshowMic = null;
            if (opts.Has("mic"))
            {
                dshowMic = DeviceResolver.ResolveName(FfmpegDevices.ListAudio(), opts.Get("mic")!);
            }

            // Camera via DirectShow (issue #28). Resolved BEFORE the session directory is created,
            // so an unknown or ambiguous camera fails leaving nothing on disk (AC8).
            string? dshowCamera = null;
            if (opts.Has("camera"))
            {
                dshowCamera = DeviceResolver.ResolveCameraName(FfmpegDevices.ListVideo(), opts.Get("camera")!);
            }
            int cameraFps = opts.Has("camera-fps") ? opts.RequireInt("camera-fps", "") : 30;

            bool mix = opts.Has("mix");                    // mic + system, mixed
            bool sysOnly = opts.Has("loopback") && !mix;   // system audio only
            bool needLoopback = mix || sysOnly;
            if (sysOnly) dshowMic = null;
            AudioSourceKind src = mix ? AudioSourceKind.Mixed
                : sysOnly ? AudioSourceKind.System
                : dshowMic != null ? AudioSourceKind.Mic
                : AudioSourceKind.None;
            var mixOpts = MixOpts(opts, src);

            int fps = opts.Has("fps") ? opts.RequireInt("fps", "") : 30;
            int crf = opts.Has("crf") ? opts.RequireInt("crf", "") : 23;

            string dir = NewSessionDir(opts, "video");
            string finalPath = Path.Combine(dir, "recording.mp4");
            // When we add system audio we mux after capture, so ffmpeg writes a raw file first.
            // A mic-only recording also gets a post pass when any mic processing is on
            // (suppression, gate, leveling, volume), so clean-voice applies there too.
            bool micPost = !needLoopback && dshowMic != null && mixOpts.MicProcessing;
            string ffOut = needLoopback || micPost ? Path.Combine(dir, "raw.mp4") : finalPath;

            var manifest = NewManifest("video", opts, monitor);
            manifest.Region = regionField;
            manifest.Microphone = mix ? $"{dshowMic} + (system)" : (sysOnly ? "(system)" : dshowMic);
            manifest.VideoFile = "recording.mp4";
            if (dshowCamera != null) manifest.CameraFile = "camera.mp4";

            string audioDesc = mix ? $"mic + system (mixed, {FxDesc(mixOpts)})"
                : sysOnly ? "system audio" : (dshowMic != null ? $"mic \"{dshowMic}\" ({FxDesc(mixOpts)})" : "video only");
            Console.WriteLine($"[ok] recording monitor {monitor.Index} ({capture.Width}x{capture.Height}) + {audioDesc}");
            Console.WriteLine("     engine: ffmpeg gdigrab" + (needLoopback ? " + WASAPI loopback" : (dshowMic != null ? " + dshow" : "")));
            Console.WriteLine("     hotkeys: S=marker screenshot  Q=stop");

            Audio.LoopbackCapture? sysCap = needLoopback ? new Audio.LoopbackCapture() : null;
            string? sysWav = needLoopback ? Path.Combine(dir, "sys_native.wav") : null;

            // The camera is opened FIRST, for the same reason the service opens it first (issue #28,
            // AC9): a camera that cannot be opened must fail the start while the directory is still
            // empty, so the failed attempt leaves nothing behind.
            //
            // Everything from here to the end of the command runs inside ONE failure boundary
            // (issue #28, gate defect 1). The camera is a live OS process holding an EXCLUSIVE
            // DirectShow device, and before this there was no finally and no using anywhere on the
            // path: gdigrab failing to open the screen - or the loopback start, the audio mux, the
            // duration probe or the manifest save throwing - unwound straight out of the command and
            // left that ffmpeg writing camera.mp4 with the webcam still taken for the life of the
            // process, and a half-written recording directory behind it.
            FfmpegCameraRecorder? cameraRec = null;
            string? cameraStopFailure = null;
            try
            {
                if (dshowCamera != null)
                {
                    // CONSTRUCTED AND ASSIGNED BEFORE FFMPEG EXISTS (issue #28, gate round 3,
                    // defect 1). The finally at the bottom of this method is this camera's last
                    // owner, and it can only own a recorder the local actually received: while
                    // opening the camera was one static call, an open failure threw before the
                    // assignment, so a stalled ffmpeg that survived the probe's kill went out of
                    // scope still holding the webcam, with `cameraRec` null and the finally
                    // disposing nothing.
                    cameraRec = FfmpegCameraRecorder.Create(dshowCamera, cameraFps, crf, Path.Combine(dir, "camera.mp4"));
                    try
                    {
                        cameraRec.Open();
                    }
                    catch
                    {
                        // Get ffmpeg off the camera FIRST. A process that survived the failed open
                        // still owns camera.mp4, so removing the directory around it would replace
                        // the real, actionable camera error with an IO error about a file in use.
                        // (Dispose is a no-op when the open already confirmed the process gone.)
                        cameraRec.Dispose();

                        // Nothing has been captured into this directory yet, and a directory holding
                        // no recording is not something to leave behind (AC8/AC9).
                        DiscardEmptyRecordingDirectory(dir);
                        throw;
                    }
                }

                using var recorder = FfmpegRecorder.Start(capture, dshowMic, fps, crf, ffOut);
                manifest.FfmpegCommand = recorder.CommandLine;
                if (cameraRec != null)
                {
                    // Alignment hint (assumption A5): negative, because the camera started first.
                    manifest.CameraStartOffsetSeconds =
                        Math.Round((cameraRec.StartedUtc - recorder.StartedUtc).TotalSeconds, 3);
                    Console.WriteLine($"     camera: \"{dshowCamera}\" -> camera.mp4 ({cameraFps} fps, video only)");
                }
                sysCap?.Start(sysWav!);

                var sw = Stopwatch.StartNew();
                var flag = new Flag();
                var keys = new Thread(() => VideoKeys(flag, sw, monitor, capture, dir, manifest, recorder))
                { IsBackground = true };
                keys.Start();

                int autoStop = ParseAutoStop(opts);
                while (!flag.Value)
                {
                    Console.Write($"\rREC {Timecodes.Label(sw.Elapsed)}   ");
                    Thread.Sleep(250);
                    if (recorder.HasExited) { Console.WriteLine("\n[warn] ffmpeg exited early; stopping."); break; }
                    if (autoStop > 0 && sw.Elapsed.TotalSeconds >= autoStop) flag.Value = true;
                }

                recorder.Stop();
                // Stopped AFTER the screen recorder, so both files carry the screen recorder's drain
                // wait and their durations stay within a second of each other.
                //
                // A camera stop that could not terminate ffmpeg (gate defect 2) is reported here and
                // carried into the exit code, but it must NOT abandon the rest of the command: the
                // screen recording is already on disk, and the manifest written below is what makes
                // it a recording rather than loose bytes. Same shape and same reason as the service's
                // failure-isolated stop sequence - nothing is hidden, the failure is printed, logged,
                // and returned.
                if (cameraRec != null)
                {
                    try
                    {
                        cameraRec.Stop();
                    }
                    catch (Exception ex)
                    {
                        cameraStopFailure = ex.Message;
                        Log.Error("[Commands] Video: stopping the camera FAILED", ex);
                        Console.WriteLine($"[fail] {ex.Message}");
                    }
                }
                sysCap?.Stop();
                sysCap?.Dispose();
                sw.Stop();
                Console.WriteLine();

                // Issue #83: the untouched pre-processing captures (raw.mp4, sys_native.wav) are
                // preserved (renamed to ".original") rather than deleted, so over-removal is recoverable.
                var originals = new System.Collections.Generic.List<string>();
                if (needLoopback)
                {
                    Console.WriteLine("     mixing audio...");
                    if (mix) AgentEyes.Audio.AudioMix.MuxVideoMixed(ffOut, sysWav!, finalPath, mixOpts);
                    else AgentEyes.Audio.AudioMix.MuxVideoSystemOnly(ffOut, sysWav!, finalPath, mixOpts.SystemGain);
                    originals.AddRange(OriginalBackup.Preserve(dir, "video", src));
                }
                else if (micPost)
                {
                    Console.WriteLine("     processing mic audio...");
                    AgentEyes.Audio.AudioMix.ProcessVideoMic(ffOut, finalPath, mixOpts);
                    originals.AddRange(OriginalBackup.Preserve(dir, "video", AudioSourceKind.Mic));
                }

                double dur = File.Exists(finalPath) ? MediaProbe.DurationSeconds(finalPath) : 0;
                manifest.DurationSeconds = Math.Round(dur > 0 ? dur : sw.Elapsed.TotalSeconds, 2);
                manifest.Files.Add("recording.mp4");
                if (cameraRec != null)
                {
                    manifest.CameraCapturedSeconds = Math.Round(cameraRec.CapturedSeconds, 2);
                    manifest.CameraTruncated = cameraRec.LostMidRun;
                    manifest.Files.Add("camera.mp4");
                    if (cameraRec.LostMidRun)
                    {
                        Console.WriteLine($"[warn] the camera \"{cameraRec.DeviceName}\" was lost during the "
                            + $"recording - camera.mp4 covers {cameraRec.CapturedSeconds:F1}s; the screen "
                            + "recording is unaffected.");
                    }
                }
                foreach (var s in manifest.Shots) manifest.Files.Add(s.File);
                foreach (var o in originals) { manifest.OriginalFiles.Add(o); manifest.Files.Add(o); }
                ManifestStore.Replace(dir, manifest);

                long size = File.Exists(finalPath) ? new FileInfo(finalPath).Length : 0;
                string sizeText = size >= 1024 * 1024
                    ? $"{size / 1024.0 / 1024.0:F1} MB"
                    : $"{size / 1024.0:F0} KB";
                Console.WriteLine($"[ok] recording.mp4 ({Timecodes.Label(TimeSpan.FromSeconds(manifest.DurationSeconds))}, {sizeText}), {manifest.Shots.Count} marker(s)");
                if (cameraRec != null)
                {
                    string camPath = Path.Combine(dir, "camera.mp4");
                    long camSize = File.Exists(camPath) ? new FileInfo(camPath).Length : 0;
                    string camSizeText = camSize >= 1024 * 1024
                        ? $"{camSize / 1024.0 / 1024.0:F1} MB"
                        : $"{camSize / 1024.0:F0} KB";
                    // "[ok]" is a CLAIM about the file, so it is only printed for a track this
                    // recording can vouch for. A camera that was lost - or that opened and never
                    // reported writing a frame - gets the warning shape instead (issue #28, gate
                    // round 3, defect 3): the file exists, and it is not a complete take.
                    Console.WriteLine(cameraRec.LostMidRun
                        ? $"[warn] camera.mp4 ({cameraRec.CapturedSeconds:F1}s, {camSizeText}), video only - TRUNCATED"
                        : $"[ok] camera.mp4 ({cameraRec.CapturedSeconds:F1}s, {camSizeText}), video only");
                }
                Console.WriteLine($"[ok] manifest.json written to {dir}");
                if (cameraStopFailure != null)
                {
                    Console.WriteLine("[fail] the camera did not stop cleanly - the screen recording and the "
                        + "manifest are on disk; see the log.");
                    return 1;
                }
                return 0;
            }
            finally
            {
                // The camera's LAST owner. Whatever happened above - a clean stop, or a throw out of
                // gdigrab, the mux, the duration probe or the manifest save - ffmpeg is stopped and
                // the webcam is handed back before this command leaves the stack.
                cameraRec?.Dispose();
            }
        }

        /// <summary>
        /// Remove the recording directory a failed camera start created, so nothing is left behind
        /// for the Library and the repair passes to find (issue #28, AC8/AC9).
        ///
        /// Its own failure is reported and NOT thrown, for the same reason
        /// <see cref="RecordingStartSequence.Abandon"/> collects rollback failures rather than
        /// raising them: the caller is already carrying the camera failure, and that is the
        /// actionable fact. Replacing "the camera is already in use by another application" with
        /// "the process cannot access the file camera.mp4" would hide the cause behind its symptom.
        /// </summary>
        private static void DiscardEmptyRecordingDirectory(string dir)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Error($"[Commands] Video: removing the empty recording directory {dir} after a failed "
                          + "camera start FAILED - it is left on disk", ex);
            }
        }

        // ---- package -------------------------------------------------------

        public static int Package(CliArgs opts)
        {
            if (opts.Positional.Count == 0)
            {
                throw new UsageException("package needs a recording directory or video file: agenteyes package <dir | video.mp4>");
            }
            double interval = opts.Has("interval") ? opts.RequireInt("interval", "") : 5.0;
            double? scene = opts.Has("scene")
                ? double.Parse(opts.Get("scene")!, System.Globalization.CultureInfo.InvariantCulture)
                : (double?)null;
            return AgentEyes.Package.Run(opts.Positional[0], interval, scene);
        }

        // ---- import --------------------------------------------------------

        public static int Import(CliArgs opts)
        {
            if (opts.Positional.Count == 0)
            {
                throw new UsageException("import needs a video file: agenteyes import <video.mp4>");
            }
            var result = VideoImport.Run(opts.Positional[0]);
            Console.WriteLine($"[ok] id={result.Id}");
            Console.WriteLine($"[ok] dir={result.Dir}");
            return 0;
        }

        // ---- translate -----------------------------------------------------

        public static int Translate(CliArgs opts)
        {
            if (opts.Positional.Count == 0)
            {
                throw new UsageException("translate needs a recording id: agenteyes translate <id> --to <lang>");
            }
            string lang = opts.Require("to", "e.g. agenteyes translate <id> --to tr");
            var result = Translator.Run(opts.Positional[0], lang);
            Console.WriteLine($"[ok] id={result.Id}");
            Console.WriteLine($"[ok] language={result.Language}, cues={result.CueCount}");
            Console.WriteLine($"[ok] dir={result.Dir}");
            return 0;
        }

        // ---- subtitle ------------------------------------------------------

        public static int Subtitle(CliArgs opts)
        {
            if (opts.Positional.Count == 0)
            {
                throw new UsageException("subtitle needs a recording id: agenteyes subtitle <id> --lang <lang>");
            }
            string lang = opts.Require("lang", "e.g. agenteyes subtitle <id> --lang tr");
            var result = SubtitleBurner.Run(opts.Positional[0], lang);
            Console.WriteLine($"[ok] id={result.Id}");
            Console.WriteLine($"[ok] language={result.Language}, output={result.OutputFile}");
            Console.WriteLine($"[ok] dir={result.Dir}");
            return 0;
        }

        // ---- shared session key handling -----------------------------------

        private static void SessionKeys(Flag stop, Stopwatch sw, MonitorInfo monitor, string dir, Manifest manifest)
        {
            while (!stop.Value)
            {
                if (Console.IsInputRedirected) { Thread.Sleep(100); continue; }
                if (!Console.KeyAvailable) { Thread.Sleep(50); continue; }
                ConsoleKey key;
                try { key = Console.ReadKey(intercept: true).Key; }
                catch { Thread.Sleep(100); continue; }

                if (key == ConsoleKey.Q) { stop.Value = true; }
                else if (key == ConsoleKey.S) { TakeShot(sw.Elapsed, monitor, monitor.Bounds, dir, manifest); }
                else if (key == ConsoleKey.P)
                {
                    Console.WriteLine("\n[todo] pause/resume not implemented for audio mode yet.");
                }
            }
        }

        private static void VideoKeys(Flag stop, Stopwatch sw, MonitorInfo monitor, Drawing.Rectangle capture,
            string dir, Manifest manifest, FfmpegRecorder recorder)
        {
            while (!stop.Value)
            {
                if (Console.IsInputRedirected) { Thread.Sleep(100); continue; }
                if (!Console.KeyAvailable) { Thread.Sleep(50); continue; }
                ConsoleKey key;
                try { key = Console.ReadKey(intercept: true).Key; }
                catch { Thread.Sleep(100); continue; }

                if (key == ConsoleKey.Q) { stop.Value = true; }
                else if (key == ConsoleKey.S) { TakeShot(sw.Elapsed, monitor, capture, dir, manifest); }
            }
        }

        private static void TakeShot(TimeSpan offset, MonitorInfo monitor, Drawing.Rectangle rect, string dir, Manifest manifest)
        {
            string shot = Path.Combine(dir, "shots", Timecodes.FileName(offset));
            Screenshot.CaptureRect(rect, shot, copyToClipboard: false);
            manifest.Shots.Add(new Manifest.ShotEntry
            {
                OffsetSeconds = Math.Round(offset.TotalSeconds, 2),
                File = Path.Combine("shots", Path.GetFileName(shot)).Replace('\\', '/'),
            });
            Console.WriteLine($"\n[ok] shot @ {Timecodes.Label(offset)}  {Path.GetFileName(shot)}");
        }

        // ---- helpers -------------------------------------------------------

        private static Manifest NewManifest(string mode, CliArgs opts, MonitorInfo monitor) => new()
        {
            Mode = mode,
            Label = opts.Get("label") ?? mode,
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            MonitorIndex = monitor.Index,
            MonitorName = monitor.Name,
        };

        private static string NewSessionDir(CliArgs opts, string mode)
        {
            string label = Sanitize(opts.Get("label") ?? mode);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string baseDir = opts.Get("out")
                             ?? Path.Combine(Environment.CurrentDirectory, "recordings", $"{stamp}_{label}");
            Directory.CreateDirectory(Path.Combine(baseDir, "shots"));
            return baseDir;
        }

        // Optional non-interactive stop after N seconds (for automated/headless runs).
        private static int ParseAutoStop(CliArgs opts) =>
            opts.Has("seconds") ? opts.RequireInt("seconds", "") : 0;

        // Build mix options from CLI flags: --no-denoise, --gate / --no-gate, --no-level,
        // --mic-vol N (percent), --sys-vol N (percent).
        private static AudioMixOptions MixOpts(CliArgs opts, AudioSourceKind src)
        {
            var m = new AudioMixOptions();
            if (opts.Has("no-denoise")) m.NoiseSuppression = false;
            // Issue #83: the gate defaults OFF for a mic-only source (no speaker bleed to tame; it
            // only risks cutting real speech) and ON for mixed/system. Either default is overridable:
            // --gate forces it on, --no-gate forces it off.
            m.NoiseGate = GateDefaults.For(src);
            if (opts.Has("gate")) m.NoiseGate = true;
            if (opts.Has("no-gate")) m.NoiseGate = false;
            if (opts.Has("no-level")) m.VoiceLeveling = false;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (opts.Has("mic-vol")) m.MicGain = double.Parse(opts.Get("mic-vol")!, ci) / 100.0;
            if (opts.Has("sys-vol")) m.SystemGain = double.Parse(opts.Get("sys-vol")!, ci) / 100.0;
            return m;
        }

        // Human-readable list of the mic processing stages that are on, e.g. "denoise+gate+level".
        private static string FxDesc(AudioMixOptions m)
        {
            var fx = new System.Collections.Generic.List<string>();
            if (m.NoiseSuppression) fx.Add("denoise");
            if (m.NoiseGate) fx.Add("gate");
            if (m.VoiceLeveling) fx.Add("level");
            return fx.Count > 0 ? string.Join("+", fx) : "no fx";
        }

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
            return s.Replace(' ', '-');
        }

        private static void RunMeterLoop(Flag flag, Func<float> peak, Stopwatch sw, int autoStop)
        {
            while (!flag.Value)
            {
                DrawMeter(peak(), sw.Elapsed);
                Thread.Sleep(200);
                if (autoStop > 0 && sw.Elapsed.TotalSeconds >= autoStop) flag.Value = true;
            }
        }

        private static void DrawMeter(float peak, TimeSpan elapsed)
        {
            const int width = 20;
            int on = (int)Math.Round(Math.Clamp(peak, 0f, 1f) * width);
            string bar = new string('|', on) + new string('-', width - on);
            Console.Write($"\rREC {Timecodes.Label(elapsed)}  mic [{bar}]   ");
        }
    }
}
