using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentEyes.Packaging;

namespace AgentEyes
{
    /// <summary>
    /// Read-only browse helpers over the recordings library and the capture gallery, shared by the
    /// loopback Control API (issue #73 - ControlApi S1). Every method is a pure read (no mutation)
    /// and reuses the existing primitives - <see cref="Manifest"/>, <see cref="RecordingPaths"/>,
    /// <see cref="CaptureService"/> - so no manifest or capture-folder logic is duplicated here.
    ///
    /// The recording-facing methods take an optional <c>root</c> (default
    /// <see cref="RecordingPaths.Root"/>) purely so they can be unit-tested against a temp
    /// directory without touching the real recordings folder.
    /// </summary>
    internal static class RecordingLibrary
    {
        /// <summary>One row of the recordings list (GET /recordings).</summary>
        internal sealed class Summary
        {
            public string Id { get; init; } = "";
            public string Dir { get; init; } = "";
            public string Label { get; init; } = "";
            public string? Title { get; init; }
            public string Mode { get; init; } = "";
            public double DurationSeconds { get; init; }
            public string CreatedUtc { get; init; } = "";
            public int ShotCount { get; init; }
            public bool HasVideo { get; init; }
            public bool HasAudio { get; init; }
            public bool HasTranscript { get; init; }
            /// <summary>A legacy flat transcript.txt exists (issue #4). Independent of
            /// <see cref="HasTranscript"/>: a flat-text-only recording is NOT transcribed but its
            /// text is still readable.</summary>
            public bool HasFlatTranscript { get; init; }
        }

        /// <summary>A page of the recordings list plus the full library total (GET /recordings).</summary>
        internal sealed class Page
        {
            public int Total { get; init; }
            public IReadOnlyList<Summary> Items { get; init; } = Array.Empty<Summary>();
        }

        /// <summary>One recording's full detail (GET /recordings/{id}).</summary>
        internal sealed class Detail
        {
            public string Id { get; init; } = "";
            public string Dir { get; init; } = "";
            public bool HasVideo { get; init; }
            public bool HasAudio { get; init; }
            public bool HasTranscript { get; init; }
            /// <summary>See <see cref="Summary.HasFlatTranscript"/> (issue #4).</summary>
            public bool HasFlatTranscript { get; init; }
            public Manifest Manifest { get; init; } = new();
        }

        /// <summary>One marker / key-frame screenshot (GET /recordings/{id}/shots).</summary>
        internal sealed class Shot
        {
            public string File { get; init; } = "";          // relative to the recording dir
            public string Path { get; init; } = "";          // absolute
            public double OffsetSeconds { get; init; }
        }

        /// <summary>A recording's transcript (GET /recordings/{id}/transcript).</summary>
        internal sealed class TranscriptView
        {
            public string Text { get; init; } = "";
            public IReadOnlyList<TranscriptLine> Segments { get; init; } = Array.Empty<TranscriptLine>();
        }

        /// <summary>One transcript line with its time window.</summary>
        internal sealed class TranscriptLine
        {
            public double Start { get; init; }
            public double End { get; init; }
            public string Text { get; init; } = "";
        }

        /// <summary>One capture-gallery item (GET /captures).</summary>
        internal sealed class CaptureItem
        {
            public string File { get; init; } = "";          // file name
            public string Path { get; init; } = "";          // absolute
            public long SizeBytes { get; init; }
            public string CreatedUtc { get; init; } = "";
        }

        private static string RootOr(string? root) =>
            string.IsNullOrWhiteSpace(root) ? RecordingPaths.Root : root!;

        /// <summary>
        /// A page of the recordings library, newest-first. <paramref name="limit"/> and
        /// <paramref name="offset"/> page over all recordings (those with a manifest.json);
        /// <see cref="Page.Total"/> is the full count regardless of paging.
        /// </summary>
        public static Page List(int limit, int offset, string? root = null)
        {
            if (limit < 0) limit = 0;
            if (offset < 0) offset = 0;
            var dirs = SessionDirs(RootOr(root));
            var items = new List<Summary>();
            foreach (var d in dirs.Skip(offset).Take(limit))
            {
                var s = TrySummary(d);
                if (s != null) items.Add(s);
            }
            return new Page { Total = dirs.Count, Items = items };
        }

        /// <summary>
        /// One recording's detail (parsed manifest + resolved media booleans + absolute dir), or
        /// null when <paramref name="id"/> is not a known recording.
        /// </summary>
        public static Detail? GetDetail(string id, string? root = null)
        {
            string? dir = ResolveDir(id, root);
            if (dir == null) return null;
            Manifest m;
            try { m = Manifest.Load(dir); } catch { return null; }
            return new Detail
            {
                Id = Path.GetFileName(dir),
                Dir = Path.GetFullPath(dir),
                HasVideo = HasVideo(dir, m),
                HasAudio = HasAudio(dir, m),
                HasTranscript = HasTranscript(dir, m),
                HasFlatTranscript = TranscriptStatus.HasFlatText(dir),
                Manifest = m,
            };
        }

        /// <summary>
        /// The marker / key-frame screenshots for a recording (empty list if it has none), or null
        /// when <paramref name="id"/> is not a known recording.
        /// </summary>
        public static IReadOnlyList<Shot>? GetShots(string id, string? root = null)
        {
            string? dir = ResolveDir(id, root);
            if (dir == null) return null;
            Manifest m;
            try { m = Manifest.Load(dir); } catch { return null; }
            var shots = new List<Shot>();
            foreach (var s in m.Shots)
            {
                string rel = s.File.Replace('\\', '/');
                shots.Add(new Shot
                {
                    File = rel,
                    Path = Path.GetFullPath(Path.Combine(dir, rel)),
                    OffsetSeconds = s.OffsetSeconds,
                });
            }
            return shots;
        }

        /// <summary>
        /// A recording's transcript as { text, segments }, or null when the recording does not
        /// exist OR exists but has no transcript artifact (the caller maps both to 404 not_found).
        /// Reads transcript.json (timestamped segments) when present, else the flat transcript.txt
        /// with empty segments.
        /// </summary>
        public static TranscriptView? GetTranscript(string id, string? root = null)
        {
            string? dir = ResolveDir(id, root);
            if (dir == null) return null;
            Manifest m;
            try { m = Manifest.Load(dir); } catch { return null; }
            return ReadTranscript(dir, m);
        }

        /// <summary>
        /// <see cref="GetTranscript"/> against an already-resolved directory + manifest, so the
        /// desktop detail view (issue #4) reads a transcript through exactly the same precedence
        /// the Control API serves: the manifest-named transcript.json first, else the flat
        /// transcript.txt. <paramref name="m"/> may be null (unreadable manifest) - the default
        /// artifact name is used then, and a still-readable flat text is still returned.
        /// </summary>
        public static TranscriptView? ReadTranscript(string dir, Manifest? m)
        {
            string jsonName = TranscriptStatus.JsonArtifactName(m);
            string jsonPath = Path.Combine(dir, jsonName);
            if (File.Exists(jsonPath))
            {
                List<TranscriptSegment>? segs = null;
                try
                {
                    segs = JsonSerializer.Deserialize<List<TranscriptSegment>>(File.ReadAllText(jsonPath));
                    if (segs == null)
                        Log.Warn($"[RecordingLibrary] ReadTranscript: {jsonName} in {dir} carries no segment list - falling through to transcript.txt.");
                }
                catch (Exception ex)
                {
                    // The flat transcript.txt below still serves the text, but the broken artifact
                    // must leave a trace (issue #4 review) - judging completion by PARSING it is
                    // issue #15; silently losing the evidence is not.
                    Log.Warn($"[RecordingLibrary] ReadTranscript: unparseable {jsonName} in {dir}: {ex.Message}");
                }
                if (segs != null)
                {
                    var lines = segs
                        .Select(g => new TranscriptLine { Start = g.StartSeconds, End = g.EndSeconds, Text = g.Text ?? "" })
                        .ToList();
                    string text = string.Join(" ", lines.Select(l => l.Text.Trim())).Trim();
                    return new TranscriptView { Text = text, Segments = lines };
                }
            }

            string txtPath = Path.Combine(dir, "transcript.txt");
            if (File.Exists(txtPath))
                return new TranscriptView { Text = File.ReadAllText(txtPath).Trim(), Segments = Array.Empty<TranscriptLine>() };

            return null;
        }

        /// <summary>
        /// The language codes a recording has a WebVTT transcript for (issue #98), sorted, or null
        /// when <paramref name="id"/> is not a known recording. Reads the manifest's per-language
        /// transcript map (<see cref="Manifest.Transcripts"/>): { lang -> "transcript.&lt;lang&gt;.vtt" }.
        /// A recording that predates the map returns an empty list (not null).
        /// </summary>
        public static IReadOnlyList<string>? TranscriptLanguages(string id, string? root = null)
        {
            string? dir = ResolveDir(id, root);
            if (dir == null) return null;
            Manifest m;
            try { m = Manifest.Load(dir); } catch { return null; }
            return m.Transcripts.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// The capture gallery (the PNGs in the resolved capture save folder), newest-first. Reuses
        /// <see cref="CaptureService.List"/> so it is exactly the same set the Capture tab shows;
        /// <paramref name="saveFolderOverride"/> is the Capture-tab save-folder override (null =
        /// the Windows Screenshots known folder).
        /// </summary>
        public static IReadOnlyList<CaptureItem> Captures(string? saveFolderOverride)
        {
            var result = new List<CaptureItem>();
            foreach (var c in CaptureService.List(saveFolderOverride))
            {
                long size;
                try { var fi = new FileInfo(c.File); size = fi.Exists ? fi.Length : 0; } catch { size = 0; }
                result.Add(new CaptureItem
                {
                    File = Path.GetFileName(c.File),
                    Path = c.File,
                    SizeBytes = size,
                    CreatedUtc = File.GetCreationTimeUtc(c.File).ToString("o"),
                });
            }
            return result;
        }

        // ---- internals ----------------------------------------------------

        /// <summary>All recording session directories (those with a manifest.json), newest-first.
        /// Session folders are named "yyyy-MM-dd_HHmmss_label", so an ordinal descending sort on
        /// the leaf name is newest-first.</summary>
        private static List<string> SessionDirs(string root) =>
            Directory.Exists(root)
                ? Directory.GetDirectories(root)
                    .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                    .ToList()
                : new List<string>();

        private static Summary? TrySummary(string dir)
        {
            Manifest m;
            try { m = Manifest.Load(dir); } catch { return null; }
            return new Summary
            {
                Id = Path.GetFileName(dir),
                Dir = Path.GetFullPath(dir),
                Label = m.Label,
                Title = m.Title,
                Mode = m.Mode,
                DurationSeconds = m.DurationSeconds,
                CreatedUtc = m.CreatedUtc,
                ShotCount = m.Shots.Count,
                HasVideo = HasVideo(dir, m),
                HasAudio = HasAudio(dir, m),
                HasTranscript = HasTranscript(dir, m),
                HasFlatTranscript = TranscriptStatus.HasFlatText(dir),
            };
        }

        /// <summary>Map an id (a recording session-directory leaf) to its absolute path, or null
        /// when it is not a known recording. Rejects path separators / traversal so an id can never
        /// escape the recordings root.</summary>
        private static string? ResolveDir(string id, string? root)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (id.IndexOfAny(new[] { '/', '\\' }) >= 0 || id.Contains("..")) return null;
            string dir = Path.Combine(RootOr(root), id);
            return Directory.Exists(dir) && File.Exists(Path.Combine(dir, "manifest.json")) ? dir : null;
        }

        private static bool HasVideo(string dir, Manifest m) => ResolveFile(dir, m.VideoFile, "recording.mp4") != null;

        private static bool HasAudio(string dir, Manifest m) => ResolveFile(dir, m.AudioFile, "audio.wav") != null;

        /// <summary>Canonical transcription completion (issue #4): the manifest-named
        /// transcript.json exists. A legacy flat transcript.txt no longer counts as "transcribed"
        /// here - it is exposed separately as <see cref="Summary.HasFlatTranscript"/> /
        /// <see cref="Detail.HasFlatTranscript"/> - so this flag and the desktop Library can no
        /// longer disagree, and for the default artifact name (the only one the pipeline writes)
        /// it also agrees with <see cref="TranscriptionBacklog.NeedsTranscription"/>. The backlog
        /// still hardcodes "transcript.json" (pre-existing); folding it onto this predicate is
        /// issue #15's centralization, not this change.</summary>
        private static bool HasTranscript(string dir, Manifest m) =>
            TranscriptStatus.IsTranscribed(dir, m);

        /// <summary>Resolve the manifest-named media file (else a known fallback name) to an
        /// existing absolute path, or null. Mirrors Package's resolution so the API's hasVideo/
        /// hasAudio agree with what the packager actually reads.</summary>
        private static string? ResolveFile(string dir, string? manifestName, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(manifestName))
            {
                string p = Path.Combine(dir, manifestName);
                if (File.Exists(p)) return p;
            }
            string fb = Path.Combine(dir, fallback);
            return File.Exists(fb) ? fb : null;
        }
    }
}
