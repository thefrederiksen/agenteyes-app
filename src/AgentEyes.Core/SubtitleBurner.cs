using System;
using System.IO;
using AgentEyes.Packaging;
using AgentEyes.Video;

namespace AgentEyes
{
    /// <summary>
    /// Issue #102: burn a recording's per-language WebVTT captions into a NEW library video via ffmpeg.
    /// Given a recording id/folder and a language code, this reads the subtitle-ready
    /// transcript.&lt;lang&gt;.vtt written by issue #98 (or produced by the translate slice #101) and renders
    /// recording.&lt;lang&gt;.subtitled.mp4 beside the source with the cues burned in using the libass
    /// <c>subtitles</c> filter (<see cref="FfmpegArgs.BurnSubtitles"/>), then registers the output in the
    /// manifest so the library lists it.
    ///
    /// The engine (in order): resolves the recording folder, resolves the source video, GUARDS that the
    /// requested language's VTT exists (a missing VTT is a hard, actionable error BEFORE ffmpeg runs, so
    /// no zero-byte output is ever left behind - AC4), reuses the existing ffmpeg integration
    /// (<see cref="Ffmpeg.Run"/> + <see cref="FfmpegLocator"/>) to render a brand-new MP4 (the source is
    /// never overwritten), and adds the output file name to <see cref="Manifest.Files"/>.
    ///
    /// The pure, side-effect-light pieces (<see cref="ResolveDir"/>, <see cref="ResolveSubtitlePath"/>,
    /// <see cref="ResolveVideoFile"/>, <see cref="OutputFileName"/>, <see cref="RegisterOutput"/>) are
    /// split out so the guards and manifest registration are unit-testable without launching ffmpeg.
    /// <see cref="Run"/> is the orchestrator that wires in the real ffmpeg process.
    /// </summary>
    internal static class SubtitleBurner
    {
        /// <summary>The library file name for a language's burned-in video, e.g.
        /// recording.tr.subtitled.mp4. Pure.</summary>
        public static string OutputFileName(string language) => $"recording.{language}.subtitled.mp4";

        /// <summary>The outcome of a burn run: the recording id, its folder, the burned language, and the
        /// produced output file name (relative to the folder).</summary>
        internal sealed record SubtitleResult(string Id, string Dir, string Language, string OutputFile);

        /// <summary>
        /// Burn transcript.&lt;lang&gt;.vtt into recording.&lt;lang&gt;.subtitled.mp4 for one recording and
        /// register the output in the manifest. Any failure surfaces the exact reason (no silent fallback);
        /// the VTT existence is checked before ffmpeg runs so a bad request leaves no partial output. Entry
        /// point for the CLI (<c>agenteyes subtitle &lt;id&gt; --lang &lt;lang&gt;</c>).
        /// </summary>
        public static SubtitleResult Run(string idOrPath, string language, string? root = null)
        {
            Log.Info($"[SubtitleBurner] Run: idOrPath={idOrPath}, lang={language}");

            string lang = Translator.NormalizeLanguage(language);
            string dir = ResolveDir(idOrPath, root);
            string id = Path.GetFileName(dir);
            var manifest = Manifest.Load(dir);

            string subtitlePath = ResolveSubtitlePath(dir, manifest, lang);
            string videoPath = ResolveVideoFile(dir, manifest);
            string outputName = OutputFileName(lang);
            string outputPath = Path.Combine(dir, outputName);

            Console.WriteLine($"[ok] burning {lang} captions into {outputName}");
            var args = FfmpegArgs.BurnSubtitles(videoPath, subtitlePath, outputPath);
            Ffmpeg.Run(args, $"burn subtitles {lang}");

            RegisterOutput(dir, outputName);

            Console.WriteLine($"[ok] wrote {outputName}");
            Log.Info($"[SubtitleBurner] Run: done id={id}, lang={lang}, output={outputName}");
            return new SubtitleResult(id, dir, lang, outputName);
        }

        // ---- resolution + guards (pure) ---------------------------------------

        /// <summary>
        /// Map an id (a recording session-directory leaf under the recordings root) OR a direct folder
        /// path to its absolute directory. Rejects path separators / traversal on a bare id so it can
        /// never escape the root. Throws <see cref="UsageException"/> (non-zero CLI exit) when no such
        /// recording exists. Pure and side-effect free.
        /// </summary>
        internal static string ResolveDir(string idOrPath, string? root)
        {
            if (string.IsNullOrWhiteSpace(idOrPath))
                throw new UsageException("subtitle needs a recording id or folder: agenteyes subtitle <id> --lang <lang>");

            // A direct folder that already holds a manifest.json.
            if (Directory.Exists(idOrPath) && File.Exists(Path.Combine(idOrPath, "manifest.json")))
                return Path.GetFullPath(idOrPath);

            // Otherwise a bare id under the recordings root (or the test override root).
            if (idOrPath.IndexOfAny(new[] { '/', '\\' }) < 0 && !idOrPath.Contains(".."))
            {
                string baseRoot = string.IsNullOrWhiteSpace(root) ? RecordingPaths.Root : root!;
                string dir = Path.Combine(baseRoot, idOrPath);
                if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "manifest.json")))
                    return Path.GetFullPath(dir);
            }

            throw new UsageException($"no recording found for '{idOrPath}'.");
        }

        /// <summary>
        /// Resolve the absolute path to the requested language's WebVTT (issue #102, AC4 guard). Prefers
        /// the manifest per-language map's file name (issue #98), else the conventional
        /// transcript.&lt;lang&gt;.vtt name; either way the file MUST exist on disk. Throws
        /// <see cref="UsageException"/> with an actionable message when it does not - no silent fallback,
        /// and because this runs before ffmpeg, no zero-byte output is produced. Pure.
        /// </summary>
        internal static string ResolveSubtitlePath(string dir, Manifest manifest, string language)
        {
            string vttName = manifest.Transcripts.TryGetValue(language, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
                ? mapped!
                : WebVtt.FileNameFor(language);
            string path = Path.Combine(dir, vttName);
            if (!File.Exists(path))
                throw new UsageException(
                    $"recording '{Path.GetFileName(dir)}' has no {WebVtt.FileNameFor(language)} to burn. "
                    + $"Transcribe or translate it into '{language}' first (e.g. agenteyes translate <id> --to {language}).");
            return path;
        }

        /// <summary>
        /// Resolve the source video to burn into (the manifest's <see cref="Manifest.VideoFile"/>, else the
        /// conventional recording.mp4); it MUST exist. Throws <see cref="UsageException"/> when the
        /// recording has no video (e.g. an audio-only session). Pure.
        /// </summary>
        internal static string ResolveVideoFile(string dir, Manifest manifest)
        {
            string videoName = string.IsNullOrWhiteSpace(manifest.VideoFile) ? "recording.mp4" : manifest.VideoFile!;
            string path = Path.Combine(dir, videoName);
            if (!File.Exists(path))
                throw new UsageException(
                    $"recording '{Path.GetFileName(dir)}' has no video ({videoName}) to burn subtitles into.");
            return path;
        }

        /// <summary>
        /// Register the burned output in the manifest as a derived output (issue #102): add
        /// <paramref name="outputName"/> to <see cref="Manifest.Files"/> (idempotent - re-burning the same
        /// language refreshes the one file and does not duplicate the entry).
        ///
        /// Issue #155: applied to the manifest as it reads NOW rather than to the copy loaded before
        /// ffmpeg burned the captions, which can take minutes on a long recording.
        /// </summary>
        internal static Manifest RegisterOutput(string dir, string outputName)
        {
            return ManifestStore.Update(dir, m =>
            {
                if (!m.Files.Contains(outputName)) m.Files.Add(outputName);
            });
        }
    }
}
