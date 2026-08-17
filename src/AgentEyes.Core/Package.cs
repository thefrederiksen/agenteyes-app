using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentEyes.Packaging;
using AgentEyes.Video;

namespace AgentEyes
{
    /// <summary>
    /// Phase 4: turn a finished recording into a readable walkthrough, fully in-process.
    ///   - Locate audio (audio.wav from Mode A, or extracted from recording.mp4 via ffmpeg).
    ///   - Transcribe with Whisper.net -> transcript.json + transcript.txt.
    ///   - For video recordings, extract content-change frames.
    ///   - Assemble walkthrough.html interleaving screenshots/frames with transcript by offset.
    ///
    /// Accepts either a recording directory (with manifest.json) or a bare video file;
    /// a bare video gets a sibling "&lt;stem&gt;_walkthrough" directory with a synthesized
    /// manifest, then the normal pipeline applies.
    /// </summary>
    internal static class Package
    {
        public static int Run(string path, double intervalSeconds = 5.0, double? sceneThreshold = null)
        {
            string dir;
            if (File.Exists(path))
            {
                dir = PrepareBareVideo(Path.GetFullPath(path));
            }
            else if (Directory.Exists(path))
            {
                if (!File.Exists(Path.Combine(path, "manifest.json")))
                {
                    throw new UsageException(
                        $"no manifest.json in {path}. To package a bare video, pass the video file itself: agenteyes package <video.mp4>");
                }
                dir = path;
            }
            else
            {
                throw new UsageException($"recording directory or video file not found: {path}");
            }

            return RunAsync(dir, intervalSeconds, sceneThreshold).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Bare video file -> a sibling "&lt;stem&gt;_walkthrough" directory with a synthesized
        /// manifest pointing back at the video. Idempotent: an existing manifest is kept,
        /// so re-packaging the same video reuses the directory.
        /// </summary>
        private static string PrepareBareVideo(string videoPath)
        {
            string dir = WalkthroughDirFor(videoPath);
            Directory.CreateDirectory(dir);

            if (!File.Exists(Path.Combine(dir, "manifest.json")))
            {
                double duration = MediaProbe.DurationSeconds(videoPath);
                var manifest = SynthesizeManifest(
                    Path.GetFileName(videoPath), duration, File.GetLastWriteTimeUtc(videoPath));
                ManifestStore.Replace(dir, manifest);
                Console.WriteLine($"  bare video: artifacts go to {dir}");
            }
            return dir;
        }

        internal static string WalkthroughDirFor(string videoPath) =>
            Path.Combine(Path.GetDirectoryName(videoPath)!,
                Path.GetFileNameWithoutExtension(videoPath) + "_walkthrough");

        internal static Manifest SynthesizeManifest(string videoFileName, double durationSeconds, DateTime createdUtc) => new()
        {
            Mode = "video",
            Label = Path.GetFileNameWithoutExtension(videoFileName),
            CreatedUtc = createdUtc.ToString("o"),
            DurationSeconds = Math.Round(durationSeconds, 2),
            VideoFile = "../" + videoFileName,
        };

        private static async Task<int> RunAsync(string dir, double intervalSeconds, double? sceneThreshold)
        {
            // Issue #77: the recording stop now defers the audio mux to the background. Complete
            // any pending mux first so the final mixed file exists before we transcribe/extract
            // frames from it. Idempotent: a no-op when nothing was deferred.
            RecordingService.FinalizePending(dir);

            var manifest = Manifest.Load(dir);
            string shotsDir = Path.Combine(dir, "shots");
            Directory.CreateDirectory(shotsDir);

            // 1) Resolve an audio source -> 16 kHz mono WAV that Whisper can read.
            string wav = ResolveAudioWav(dir, manifest);

            // 2) For video recordings, pull content-change frames into shots/.
            var contentShots = new List<WalkthroughShot>();
            string? videoPath = FindFirst(dir, manifest.VideoFile, "recording.mp4");
            if (videoPath != null)
            {
                Console.WriteLine(sceneThreshold.HasValue
                    ? $"  extracting scene-cut frames (threshold {sceneThreshold}) ..."
                    : $"  extracting key frames (1 every {intervalSeconds:F0}s) ...");
                contentShots.AddRange(ExtractContentFrames(videoPath, shotsDir, intervalSeconds, sceneThreshold));
                Console.WriteLine($"  {contentShots.Count} frame(s)");
            }

            // 3) Transcribe the recording through the signed-in DevThrottle account (issue #87).
            // DevThrottle is the only recording transcription path - no alternate provider. A failure
            // (not signed in, out of credits) surfaces with the exact reason.
            Console.WriteLine("  transcribing (DevThrottle, whisper-large-v3) ...");
            List<TranscriptSegment> segments;
            try
            {
                segments = await Transcriber.TranscribeWavAsync(wav);
            }
            catch (AgentEyes.DevThrottle.DevThrottleException dex)
            {
                Console.WriteLine($"  transcription FAILED: {dex.Message}");
                throw new UsageException(dex.Message, dex);
            }
            Console.WriteLine($"  {segments.Count} transcript segment(s)");

            // 3a) Dictionary corrections: the user dictionary fixes recording transcripts -
            // known misheard forms are replaced deterministically BEFORE the transcript is
            // written and before title generation, so everything downstream benefits.
            int fixes = TranscriptDictionary.Apply(segments, Transcription.DictionaryStore.Load());
            if (fixes > 0)
            {
                Console.WriteLine($"  dictionary: fixed {fixes} misheard term(s)");
                Log.Info($"transcript dictionary: {fixes} replacement(s) in {dir}");
            }

            WriteTranscript(dir, segments);

            // 3b) Short title + description from the transcript (DevThrottle chat; skipped when not
            // signed in). A naming failure must not cost the walkthrough - report and go on.
            // Issue #155: what this pass produced is held in LOCALS and applied to the manifest at
            // the end, against a fresh read - never to the copy loaded before transcription started.
            TitleGenerator.TitleResult? named = null;
            if (segments.Count > 0 && TitleGenerator.IsConfigured)
            {
                Console.WriteLine($"  naming the recording ({TitleGenerator.Model}) ...");
                try
                {
                    named = await TitleGenerator.GenerateAsync(segments);
                    Console.WriteLine($"  title: {named.Title}  "
                        + $"({(named.Usage?.PromptTokens ?? 0) + (named.Usage?.CompletionTokens ?? 0)} tokens)");
                }
                catch (Exception ex)
                {
                    // Non-fatal by design - a naming failure must never cost the transcript. But it
                    // must not be INVISIBLE either: Console output goes nowhere in the WPF app, so the
                    // only signal used to be a recording that silently kept its preset name (#140).
                    Console.WriteLine($"  [warn] title generation failed: {ex.Message}");
                    Log.Error($"[Package] title generation FAILED for {dir}: {ex.Message} - "
                        + "the recording keeps its preset name", ex);
                }
            }

            // 4) Assemble the on-demand session shots (from the manifest) + content frames.
            var shots = new List<WalkthroughShot>();
            foreach (var s in manifest.Shots)
            {
                shots.Add(new WalkthroughShot { OffsetSeconds = s.OffsetSeconds, RelativePath = s.File.Replace('\\', '/') });
            }
            shots.AddRange(contentShots);

            // 5) Record what this pass produced, into whatever the manifest says NOW. This runs
            // BEFORE the walkthrough is built (issue #155): the heading is the recording's name, and
            // the copy loaded before transcription started does not know about a rename made while
            // this pass was working. FinalizeManifest returns the manifest as it was written, so the
            // heading is built from the current name rather than a stale one.
            var finalized = FinalizeManifest(dir, contentShots, named);

            string title = $"AgentEyes walkthrough - {finalized.DisplayName ?? finalized.Title ?? finalized.Label}";
            string html = WalkthroughBuilder.Build(title, shots, segments);
            string walkthroughPath = Path.Combine(dir, "walkthrough.html");
            File.WriteAllText(walkthroughPath, html);
            Console.WriteLine("  assembling walkthrough (HTML) ... done");

            Console.WriteLine($"[ok] walkthrough.html + transcript.json written to {dir}");
            return 0;
        }

        /// <summary>
        /// Write what the packaging pass produced into the recording's manifest (issue #155).
        ///
        /// This is the fix for the concrete race in the issue: packaging loads a manifest, spends
        /// minutes transcribing and naming, and used to save that stale in-memory copy - erasing a
        /// rename (or an attempt counter, or a stage record) written by any other path in between.
        /// It now applies ONLY the fields this pass owns, to a manifest read inside
        /// <see cref="ManifestStore.Update"/> immediately before the write.
        ///
        /// The key frames are persisted into the shot list so every consumer reads the screenshots
        /// from one place - plugins included (docs/plugins.md promises manifest.Shots holds the
        /// extracted key frames). Without this, only manual marker shots reach the manifest and a
        /// plugin sees zero screenshots for a video recording.
        ///
        /// <paramref name="named"/> is null when naming was skipped or failed, and then the
        /// recording's existing title/description/AI cost are left exactly as they are. When it is
        /// not null, its token usage is ADDED to the recording's running AI cost rather than
        /// assigned over it (issue #155): a translation recorded before this pass must survive it.
        ///
        /// Called BEFORE walkthrough.html is written, so the caller can build the heading from the
        /// name this returns. The manifest therefore names walkthrough.html a moment before the file
        /// exists; every consumer of that field resolves it against the disk, and the alternative -
        /// a heading built from a name that may already be stale - is the defect being fixed.
        /// </summary>
        internal static Manifest FinalizeManifest(
            string dir,
            IReadOnlyList<WalkthroughShot> contentShots,
            TitleGenerator.TitleResult? named)
        {
            Log.Info($"[Package] FinalizeManifest: dir={dir}, frames={contentShots.Count}, named={(named != null)}");
            return ManifestStore.Update(dir, m =>
            {
                PersistFrames(m, contentShots);
                m.Transcript = "transcript.json";
                m.Transcripts[WebVtt.DefaultLanguage] = WebVtt.FileNameFor(WebVtt.DefaultLanguage);
                m.Walkthrough = "walkthrough.html";
                if (named != null)
                {
                    m.Title = named.Title;
                    m.Description = named.Description;
                    // Issue #155: ADDED to the recording's running AI cost, never assigned over it.
                    // Assigning was enough to lose real accounting with no concurrency at all - a
                    // translation recorded before this pass ran was simply erased.
                    m.AiCost = Ai.AiCostLedger.Add(m.AiCost, named.Usage, named.Model);
                }
            });
        }

        private static string ResolveAudioWav(string dir, Manifest manifest)
        {
            // Whisper needs 16 kHz mono. Always normalize the source (mic wav is already 16k mono;
            // mixed/system wav is 48k stereo; video has its audio in the mp4) so transcription is
            // correct regardless of how it was recorded.
            string? media = FindFirst(dir, manifest.AudioFile, "audio.wav")
                            ?? FindFirst(dir, manifest.VideoFile, "recording.mp4");
            if (media == null)
            {
                throw new UsageException($"no audio.wav or recording.mp4 to transcribe in {dir}.");
            }

            string outWav = Path.Combine(dir, "audio_16k.wav");
            Ffmpeg.Run(FfmpegArgs.ExtractWav(media, outWav), "extract audio");
            return outWav;
        }

        /// <summary>
        /// Merge the extracted key frames into <c>manifest.Shots</c> so the walkthrough,
        /// plugins, and any re-package all read the screenshots from the manifest.
        /// Idempotent: the <c>shots/frame_*</c> entries are rebuilt each run while manual
        /// marker shots are preserved; the result is ordered by offset.
        /// </summary>
        internal static void PersistFrames(Manifest manifest, IReadOnlyList<WalkthroughShot> contentShots)
        {
            manifest.Shots.RemoveAll(s => s.File.Replace('\\', '/').Contains("shots/frame_"));
            foreach (var cs in contentShots)
                manifest.Shots.Add(new Manifest.ShotEntry { OffsetSeconds = cs.OffsetSeconds, File = cs.RelativePath });
            manifest.Shots.Sort((a, b) => a.OffsetSeconds.CompareTo(b.OffsetSeconds));
        }

        private static IEnumerable<WalkthroughShot> ExtractContentFrames(
            string videoPath, string shotsDir, double intervalSeconds, double? sceneThreshold)
        {
            string pattern = Path.Combine(shotsDir, "frame_%03d.png");

            if (sceneThreshold.HasValue)
            {
                Ffmpeg.Run(FfmpegArgs.SceneExtract(videoPath, sceneThreshold.Value, pattern), "scene extract");
            }
            else
            {
                Ffmpeg.Run(FfmpegArgs.IntervalExtract(videoPath, intervalSeconds, pattern), "key-frame extract");
            }

            var result = new List<WalkthroughShot>();
            var files = Directory.GetFiles(shotsDir, "frame_*.png").OrderBy(p => p).ToList();
            for (int i = 0; i < files.Count; i++)
            {
                // Interval mode gives real offsets (i * interval). Scene mode has no exact offset,
                // so group those at the end with a large pseudo-offset.
                double offset = sceneThreshold.HasValue ? 1_000_000 + i : i * intervalSeconds;
                result.Add(new WalkthroughShot
                {
                    OffsetSeconds = offset,
                    RelativePath = "shots/" + Path.GetFileName(files[i]),
                });
            }
            return result;
        }

        internal static void WriteTranscript(string dir, IReadOnlyList<TranscriptSegment> segments)
        {
            var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(Path.Combine(dir, "transcript.json"),
                System.Text.Json.JsonSerializer.Serialize(segments, jsonOpts));

            using (var txt = new StreamWriter(Path.Combine(dir, "transcript.txt")))
            {
                foreach (var s in segments)
                {
                    txt.WriteLine($"[{Timecodes.Clock(TimeSpan.FromSeconds(s.StartSeconds))}] {s.Text}");
                }
            }

            // Issue #98: the subtitle-ready, cross-tool WebVTT artifact, written alongside the
            // unchanged json/txt. transcript.<lang>.vtt is the first-class transcript surface.
            string vttName = WebVtt.FileNameFor(WebVtt.DefaultLanguage);
            File.WriteAllText(Path.Combine(dir, vttName), WebVtt.Write(segments));
        }

        private static string? FindFirst(string dir, string? manifestName, string fallbackName)
        {
            if (!string.IsNullOrWhiteSpace(manifestName))
            {
                string p = Path.Combine(dir, manifestName);
                if (File.Exists(p)) return p;
            }
            string fb = Path.Combine(dir, fallbackName);
            return File.Exists(fb) ? fb : null;
        }
    }
}
