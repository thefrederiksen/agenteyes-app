using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AgentEyes.Video;

namespace AgentEyes
{
    /// <summary>
    /// Library card thumbnails (issue #19). One file per recording directory:
    ///   - video: poster frame ~10% in, 480px wide -> thumb.jpg
    ///   - audio: waveform tile on the card surface color -> thumb.png
    ///   - shot:  none here; the screenshot itself is the card image
    /// Ensure() is idempotent and safe to call from a background backfill loop.
    /// </summary>
    internal static class Thumbnails
    {
        /// <summary>
        /// How many automatic attempts one recording gets at its thumbnail (issue #142). Its own
        /// ceiling since issue #148 split the shared one, because its cost argument is its own: a
        /// thumbnail is one local ffmpeg run - no upload, no credits - so the only cost is CPU while
        /// the user is working. Three is enough to ride out a file that was still being written, and
        /// low enough that a file ffmpeg can never read does not run ffmpeg on every 15-minute tick
        /// forever.
        /// </summary>
        public const int MaxThumbAttempts = 3;

        /// <summary>Existing thumbnail file for a recording dir, or null.</summary>
        public static string? PathFor(string dir)
        {
            string jpg = Path.Combine(dir, "thumb.jpg");
            if (File.Exists(jpg)) return jpg;
            string png = Path.Combine(dir, "thumb.png");
            if (File.Exists(png)) return png;
            return null;
        }

        /// <summary>Create the thumbnail if missing. Returns its path, or null when this
        /// recording type has nothing to generate (screenshots use the shot directly).
        /// Issue #141: every null return is logged with the directory and the reason - a silent
        /// null is how the missing-poster bug stayed invisible for three recordings.</summary>
        public static string? Ensure(string dir)
        {
            string? existing = PathFor(dir);
            if (existing != null) return existing;

            Manifest m;
            try { m = Manifest.Load(dir); }
            catch (Exception ex)
            {
                Log.Info($"[Thumbnails] Ensure: no thumbnail for {dir} - manifest could not be read: {ex.Message}");
                return null;
            }

            switch (m.Mode)
            {
                case "video":
                {
                    string video = Path.Combine(dir, m.VideoFile ?? "recording.mp4");
                    if (!File.Exists(video))
                    {
                        // Issue #77 defers the audio mux out of the stop, so recording.mp4 does not
                        // exist until FinalizePending has run. Callers generate the thumbnail after
                        // that pass (issue #141); reaching here means it was asked for too early.
                        Log.Info($"[Thumbnails] Ensure: no thumbnail for {dir} - video file not found: {video}");
                        return null;
                    }
                    string outFile = Path.Combine(dir, "thumb.jpg");
                    // ~10% in, clamped so the seek stays inside very short clips.
                    double at = Math.Max(0.0, Math.Min(m.DurationSeconds * 0.10,
                        Math.Max(0.0, m.DurationSeconds - 0.2)));
                    Ffmpeg.Run(new[]
                    {
                        "-y",
                        "-ss", at.ToString("0.##", CultureInfo.InvariantCulture),
                        "-i", video,
                        "-frames:v", "1",
                        "-vf", "scale=480:-2",
                        "-q:v", "4",
                        outFile,
                    }, "poster thumbnail");
                    if (File.Exists(outFile)) return outFile;
                    Log.Info($"[Thumbnails] Ensure: no thumbnail for {dir} - ffmpeg wrote no poster frame to {outFile}");
                    return null;
                }
                case "audio":
                {
                    string audio = Path.Combine(dir, m.AudioFile ?? "audio.wav");
                    if (!File.Exists(audio))
                    {
                        // Same deferred-mux window as video: a system/mixed audio recording only
                        // gets its final audio.wav once FinalizePending has run (issue #141).
                        Log.Info($"[Thumbnails] Ensure: no thumbnail for {dir} - audio file not found: {audio}");
                        return null;
                    }
                    string outFile = Path.Combine(dir, "thumb.png");
                    // Waveform tile, 16:9 like the video posters, on the surface color.
                    Ffmpeg.Run(new[]
                    {
                        "-y",
                        "-i", audio,
                        "-filter_complex",
                        "[0:a]aformat=channel_layouts=mono,showwavespic=s=480x270:colors=0x22C55E[wave];"
                            + "color=c=0x24262B:s=480x270[bg];[bg][wave]overlay=format=auto",
                        "-frames:v", "1",
                        outFile,
                    }, "waveform thumbnail");
                    if (File.Exists(outFile)) return outFile;
                    Log.Info($"[Thumbnails] Ensure: no thumbnail for {dir} - ffmpeg wrote no waveform to {outFile}");
                    return null;
                }
                default:
                    // "shot" and anything else: the card image is the artifact itself.
                    Log.Info($"[Thumbnails] Ensure: no thumbnail for {dir} - mode '{m.Mode}' has none to generate");
                    return null;
            }
        }

        // ---- thumbnail repair backlog (issue #142) --------------------------

        /// <summary>
        /// True when <paramref name="dir"/> is a recording whose thumbnail is missing and CAN be
        /// generated now: media on disk, a mode that has a thumbnail, no thumb file, and attempts
        /// left. Detection is stateless like the transcription backlog - the truth is the files in
        /// the directory, not a bookkeeping record that can drift.
        /// </summary>
        public static bool NeedsThumb(string dir) => NeedsThumb(dir, respectClaim: true);

        /// <summary>
        /// <see cref="NeedsThumb(string)"/> for a caller that already OWNS this recording's
        /// <see cref="RecordingWorkset"/> claim (issue #152). The post-recording sequence claims the
        /// directory for the whole run, so asking the scan-time question there would have the owner's
        /// own claim veto its own thumbnail stage.
        /// </summary>
        /// <param name="respectClaim">true for a scan (skip a recording someone else holds); false
        /// for the holder of the claim itself.</param>
        public static bool NeedsThumb(string dir, bool respectClaim)
        {
            if (!Directory.Exists(dir)) return false;
            if (PathFor(dir) != null) return false;
            if (!File.Exists(Path.Combine(dir, "manifest.json"))) return false;

            // Someone is already writing to this recording - a stop pass mixing and packaging it, a
            // walkthrough rebuild, a title repair. NoteThumbAttempt is a load-mutate-save of
            // manifest.json and Package.Run writes the same file, so repairing a claimed recording
            // is a race that silently drops whichever field the loser wrote. It is not lost work:
            // the recording comes back on the next pass, once the claim is released.
            if (respectClaim && RecordingWorkset.IsClaimed(dir))
            {
                Log.Info($"[Thumbnails] NeedsThumb: skipping {Path.GetFileName(dir)} - work is in flight for it");
                return false;
            }

            Manifest m;
            try { m = Manifest.Load(dir); }
            catch (Exception ex)
            {
                Log.Info($"[Thumbnails] NeedsThumb: skipping {dir} - manifest could not be read: {ex.Message}");
                return false;
            }

            // A screenshot's card image IS the shot; there is nothing to generate.
            if (m.Mode != "video" && m.Mode != "audio") return false;

            // The final media file only exists once the deferred mux has run (issues #77/#141).
            // Asking ffmpeg for a poster before then produces nothing, so such a recording is not
            // part of the backlog yet - it comes back on the next pass, once the mux has landed.
            string media = m.Mode == "video"
                ? Path.Combine(dir, m.VideoFile ?? "recording.mp4")
                : Path.Combine(dir, m.AudioFile ?? "audio.wav");
            if (!File.Exists(media)) return false;

            return m.ThumbAttempts < MaxThumbAttempts;
        }

        /// <summary>
        /// Every recording under <paramref name="root"/> whose thumbnail is missing, oldest first so
        /// the backlog clears in the order it was created.
        /// </summary>
        public static IReadOnlyList<string> FindMissing(string root)
        {
            if (!Directory.Exists(root)) return Array.Empty<string>();

            var missing = Directory.GetDirectories(root)
                .Where(NeedsThumb)
                .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
                .ToList();

            Log.Info($"[Thumbnails] FindMissing: root={root}, missing={missing.Count}");
            return missing;
        }

        /// <summary>
        /// Records one more thumbnail attempt against the recording. Counted BEFORE the attempt
        /// runs, exactly like the transcription pass: a file that hard-fails ffmpeg every time must
        /// drop out of the automatic pass instead of being retried on every tick forever.
        /// </summary>
        public static void NoteThumbAttempt(string dir)
        {
            if (!File.Exists(Path.Combine(dir, "manifest.json")))
            {
                Log.Info($"[Thumbnails] NoteThumbAttempt: no manifest at {dir}; nothing to record");
                return;
            }

            var m = ManifestStore.Update(dir, manifest => manifest.ThumbAttempts++);
            Log.Info($"[Thumbnails] NoteThumbAttempt: {Path.GetFileName(dir)} attempt {m.ThumbAttempts}/{MaxThumbAttempts}");
        }
    }
}
