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
    /// Issue #100: import an EXISTING external video file (e.g. a Teams meeting recording) into the
    /// AgentEyes library so it becomes a normal library recording - video + transcript +
    /// title/description - reusing the same pipeline native recordings use.
    ///
    /// The engine (in order): validates the source, copies the video into a new session folder under
    /// the recordings root (assumption A1 - the user's source file is left in place), writes a
    /// <see cref="Manifest"/> that marks the entry <see cref="Manifest.Imported"/> = true and records
    /// the original source file name, extracts 16 kHz mono audio via the existing ffmpeg path,
    /// transcribes it through the shared <see cref="Transcriber"/> (segments-aware, issue #99), writes
    /// transcript.json / transcript.txt / transcript.&lt;lang&gt;.vtt via <see cref="Package.WriteTranscript"/>
    /// (issue #98), and names the recording the same way native recordings do (<see cref="TitleGenerator"/>).
    ///
    /// The pure, side-effect-free pieces (<see cref="ValidateSource"/>, <see cref="BuildManifest"/>,
    /// <see cref="WriteArtifacts"/>) are split out so construction is unit-testable with a fixture and
    /// mocked transcript segments - no ffmpeg, no network. <see cref="RunAsync"/> is the orchestrator
    /// that wires in the real ffmpeg extraction and hosted transcription.
    /// </summary>
    internal static class VideoImport
    {
        /// <summary>
        /// Accepted input container extensions (assumption A2 - the common containers ffmpeg handles).
        /// A file whose extension is not in this set is rejected by <see cref="ValidateSource"/> with a
        /// clear error rather than silently attempted.
        /// </summary>
        internal static readonly string[] VideoExtensions =
        {
            ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".ts", ".m2ts", ".mpg", ".mpeg",
        };

        /// <summary>The outcome of an import: the new recording's id (session-folder leaf) and its
        /// absolute folder path.</summary>
        internal sealed record ImportResult(string Id, string Dir);

        /// <summary>
        /// Guard the source path (issue #100, AC4): the file must exist and carry a known video
        /// extension. Throws <see cref="UsageException"/> (maps to a non-zero CLI exit) with an
        /// actionable message on failure - NO silent fallback. Pure and side-effect free.
        /// </summary>
        public static void ValidateSource(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new UsageException("import needs a video file path: agenteyes import <video.mp4>");

            if (!File.Exists(path))
                throw new UsageException($"video file not found: {path}");

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(VideoExtensions, ext) < 0)
                throw new UsageException(
                    $"'{Path.GetFileName(path)}' is not a supported video file (extension '{ext}'). " +
                    $"Accepted: {string.Join(", ", VideoExtensions)}.");
        }

        /// <summary>
        /// Build the manifest for an imported recording (issue #100, AC3): <see cref="Manifest.Mode"/>
        /// stays "video" (the artifact is a video recording) while <see cref="Manifest.Imported"/> is
        /// set true and <see cref="Manifest.ImportedSource"/> records the original source file name.
        /// The video is copied into the folder preserving its name, so <see cref="Manifest.VideoFile"/>
        /// is that same file name. Pure and side-effect free.
        /// </summary>
        internal static Manifest BuildManifest(string sourceFileName, double durationSeconds, DateTime createdUtc) => new()
        {
            Mode = "video",
            Imported = true,
            ImportedSource = sourceFileName,
            Label = Path.GetFileNameWithoutExtension(sourceFileName),
            CreatedUtc = createdUtc.ToString("o"),
            // Explicit MidpointRounding documents intent - ToEven is Math.Round's default, so the
            // produced value is unchanged; stating it makes the rounding policy intentional (issue #114).
            DurationSeconds = Math.Round(durationSeconds, 2, MidpointRounding.ToEven),
            VideoFile = sourceFileName,
        };

        /// <summary>
        /// Write the transcript artifacts for an imported recording and finalize its manifest (issue
        /// #100, AC1/AC5). Reuses <see cref="Package.WriteTranscript"/> so the imported recording gets
        /// exactly the same transcript.json / transcript.txt / transcript.&lt;lang&gt;.vtt a native
        /// recording gets - multiple cues when transcription returned multiple segments, a single valid
        /// cue on the single-segment fallback (issue #99). Sets the per-language transcript map (issue
        /// #98). Pure with respect to ffmpeg/network - the segments are supplied by the caller, so this
        /// is unit-testable with a fixture.
        ///
        /// Issue #155: the manifest is finalized through <see cref="ManifestStore.Update"/>, so the
        /// import's record must already be on disk (<see cref="RunAsync"/> writes it before
        /// transcription starts) and the title arrives here as <paramref name="named"/> rather than
        /// pre-applied to a copy that has been held across the whole transcription.
        /// </summary>
        internal static Manifest WriteArtifacts(
            string dir, IReadOnlyList<TranscriptSegment> segments, TitleGenerator.TitleResult? named)
        {
            Package.WriteTranscript(dir, segments);
            return ManifestStore.Update(dir, m =>
            {
                m.Transcript = "transcript.json";
                m.Transcripts[WebVtt.DefaultLanguage] = WebVtt.FileNameFor(WebVtt.DefaultLanguage);
                if (named != null)
                {
                    m.Title = named.Title;
                    m.Description = named.Description;
                    // Issue #155: ADDED to the running AI cost, never assigned over it.
                    m.AiCost = AgentEyes.Ai.AiCostLedger.Add(m.AiCost, named.Usage, named.Model);
                }
            });
        }

        /// <summary>Synchronous entry point for the CLI (<c>agenteyes import &lt;file&gt;</c>).</summary>
        public static ImportResult Run(string sourcePath) => RunAsync(sourcePath).GetAwaiter().GetResult();

        /// <summary>
        /// Orchestrate a full import: validate -> verify a video stream -> create the session folder ->
        /// copy the video in -> transcribe -> name -> write artifacts. All logging/console output is
        /// ASCII. A failure at any step surfaces the exact reason (no silent fallback).
        /// </summary>
        internal static async Task<ImportResult> RunAsync(string sourcePath)
        {
            Log.Info($"[VideoImport] RunAsync: source={sourcePath}");
            ValidateSource(sourcePath);
            string source = Path.GetFullPath(sourcePath);
            string sourceFileName = Path.GetFileName(source);

            // Probe the SOURCE before creating anything, so a bad file leaves no orphan folder. A
            // container with a real video extension but no video stream is rejected here (AC4).
            var (hasVideo, _) = MediaProbe.Streams(source);
            if (!hasVideo)
                throw new UsageException($"'{sourceFileName}' contains no video stream - nothing to import.");
            double duration = MediaProbe.DurationSeconds(source);

            string dir = RecordingPaths.NewDir("video", Path.GetFileNameWithoutExtension(sourceFileName));
            Console.WriteLine($"[ok] importing '{sourceFileName}' -> {dir}");

            // A1: copy (not move) so the user's original file stays in place.
            string destVideo = Path.Combine(dir, sourceFileName);
            File.Copy(source, destVideo, overwrite: true);
            Console.WriteLine($"  copied video ({Timecodes.Label(TimeSpan.FromSeconds(duration))})");

            var manifest = BuildManifest(sourceFileName, duration, File.GetLastWriteTimeUtc(source));
            manifest.Files.Add(sourceFileName);
            ManifestStore.Replace(dir, manifest);   // a directory this import just created (issue #155)

            // Extract 16 kHz mono audio via the existing ffmpeg path, then transcribe through the
            // shared segments-aware Transcriber (DevThrottle-hosted Whisper, issue #99).
            string wav = Path.Combine(dir, "audio_16k.wav");
            Ffmpeg.Run(FfmpegArgs.ExtractWav(destVideo, wav), "extract audio");

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

            // Same deterministic dictionary corrections native recordings get, applied before the
            // transcript is written and before naming so everything downstream benefits.
            int fixes = TranscriptDictionary.Apply(segments, Transcription.DictionaryStore.Load());
            if (fixes > 0) Console.WriteLine($"  dictionary: fixed {fixes} misheard term(s)");

            // Name the recording exactly the way native recordings are named (skipped when not signed
            // in). A naming failure must not cost the import - report and go on.
            // Issue #155: held in a local and applied by WriteArtifacts to a fresh read, not to the
            // copy this method has been carrying since before transcription.
            TitleGenerator.TitleResult? named = null;
            if (segments.Count > 0 && TitleGenerator.IsConfigured)
            {
                Console.WriteLine($"  naming the recording ({TitleGenerator.Model}) ...");
                try
                {
                    named = await TitleGenerator.GenerateAsync(segments);
                    Console.WriteLine($"  title: {named.Title}");
                }
                catch (Exception ex)
                {
                    // Non-fatal by design - a naming failure must never cost the import. But it must
                    // not be INVISIBLE either: Console output goes nowhere in the WPF app, so the only
                    // signal used to be an import that silently kept its generic name (#140).
                    Console.WriteLine($"  [warn] title generation failed: {ex.Message}");
                    Log.Error($"[VideoImport] title generation FAILED for {dir}: {ex.Message} - "
                        + "the recording keeps its generic name", ex);
                }
            }

            WriteArtifacts(dir, segments, named);

            // Issue #142: give the imported recording its Library poster here. The Library list used
            // to generate thumbnails as a side effect of loading, uncounted and unbounded; that path
            // is gone, so the import - the one place that knows a new recording just landed -
            // produces it. Non-fatal by design, exactly like the naming step above: an import that
            // succeeded must not be reported as failed because ffmpeg could not draw a poster, and
            // the repair pass picks it up on its next run.
            try { Thumbnails.Ensure(dir); }
            catch (Exception ex) { Log.Error($"[VideoImport] thumbnail FAILED for {dir}: {ex.Message}", ex); }

            string id = Path.GetFileName(dir);
            Console.WriteLine($"[ok] imported recording {id}");
            Console.WriteLine($"[ok] folder: {Path.GetFullPath(dir)}");
            Log.Info($"[VideoImport] RunAsync: done id={id}, segments={segments.Count}");
            return new ImportResult(id, Path.GetFullPath(dir));
        }
    }
}
