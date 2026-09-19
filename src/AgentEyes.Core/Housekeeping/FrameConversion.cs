using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Drawing = System.Drawing;
using Imaging = System.Drawing.Imaging;

namespace AgentEyes.Housekeeping
{
    /// <summary>What converting one recording's frames achieved (issues #55, #56).</summary>
    internal sealed class FrameConversionResult
    {
        public int Converted { get; init; }
        public int Failed { get; init; }

        /// <summary>Frames already converted by an earlier, interrupted pass and finished by this one.</summary>
        public int Repaired { get; init; }

        public long BytesBefore { get; init; }
        public long BytesAfter { get; init; }
        public long BytesReclaimed => BytesBefore - BytesAfter;

        /// <summary>Why it gave up before starting. Null when it ran.</summary>
        public string? Error { get; init; }
    }

    /// <summary>
    /// Re-encode a recording's extracted walkthrough frames from PNG to JPEG, and keep the two things
    /// that POINT at them honest - the manifest's shot list and walkthrough.html (issues #55, #56).
    ///
    /// The frames are the largest item in a mature library: 11.52 GB across 18,823 files on the machine
    /// this was measured on, against 5.25 GB of actual video. They are also the safest large thing to
    /// touch, because they are DERIVED - Package extracts them from recording.mp4 at the interval the
    /// manifest records, so one can be made again. That is the whole justification, and it does not
    /// extend to anything else in the directory.
    ///
    /// FOUR PHASES, IN THIS ORDER, AND THE ORDER IS THE SAFETY. Encode every frame while keeping its
    /// PNG; work out what should now be referenced; repoint the manifest and the page; only then delete
    /// the PNGs. At every boundary, every reference in the manifest and the page resolves to a file that
    /// exists.
    ///
    /// The first version deleted each PNG as it converted and repointed at the end, and a run stopped
    /// part way through left 268 references in one recording pointing at deleted files - a destroyed
    /// walkthrough. The doc comment on that version claimed a crash was safe at any moment. It was not.
    /// Phase 2 rebuilds the reference map from the JPEGs actually on disk rather than from what this pass
    /// happened to convert, which is what lets a later run FINISH an interrupted one instead of leaving
    /// the damage in place.
    /// </summary>
    internal static class FrameConversion
    {
        /// <summary>The page that references the frames by relative path.</summary>
        public const string WalkthroughFile = "walkthrough.html";

        /// <summary>
        /// Convert every extracted frame in <paramref name="dir"/>, and finish any conversion an earlier
        /// pass left half done.
        /// </summary>
        /// <param name="dir">The recording directory.</param>
        /// <param name="quality">JPEG quality, 1 to 100.</param>
        public static FrameConversionResult Run(string dir, int quality)
        {
            string shots = Path.Combine(dir, HousekeepingPlan.FramesDirectory);
            if (!Directory.Exists(shots))
            {
                return new FrameConversionResult { Error = "the recording has no shots directory" };
            }

            var pngs = Directory.GetFiles(shots, "frame_*.png")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Frames a previous run already encoded. Present even when there is no PNG left to convert,
            // which is exactly the interrupted case that has to be finishable.
            var existingJpgs = new HashSet<string>(
                Directory.GetFiles(shots, "frame_*.jpg").Select(Path.GetFileName)!,
                StringComparer.OrdinalIgnoreCase);

            if (pngs.Count == 0 && existingJpgs.Count == 0)
            {
                return new FrameConversionResult { Error = "no extracted frames to convert" };
            }

            long before = 0, after = 0;
            int converted = 0, failed = 0;

            // ---- phase 1: encode, keeping every PNG ------------------------------------------
            foreach (string png in pngs)
            {
                string jpg = Path.ChangeExtension(png, ".jpg");
                string jpgName = Path.GetFileName(jpg);

                long pngBytes;
                try
                {
                    pngBytes = new FileInfo(png).Length;
                }
                catch (IOException ex)
                {
                    Log.Warn($"[FrameConversion] could not size {png}: {ex.Message}");
                    failed++;
                    continue;
                }

                try
                {
                    if (!existingJpgs.Contains(jpgName))
                    {
                        EncodeJpeg(png, jpg, quality);
                    }

                    long jpgBytes = new FileInfo(jpg).Length;
                    if (jpgBytes <= 0) throw new InvalidOperationException("the encoder produced an empty file");

                    // A frame that did not get smaller is left as PNG. Re-encoding it would spend a lossy
                    // pass to gain nothing, which is the worst of both.
                    if (jpgBytes >= pngBytes)
                    {
                        File.Delete(jpg);
                        existingJpgs.Remove(jpgName);
                        before += pngBytes;
                        after += pngBytes;
                        continue;
                    }

                    existingJpgs.Add(jpgName);
                    before += pngBytes;
                    after += jpgBytes;
                    converted++;
                }
                catch (Exception ex)
                {
                    // One unreadable frame must not cost the other seven hundred. The PNG is untouched,
                    // so nothing is lost and the next pass tries again.
                    Log.Warn($"[FrameConversion] {Path.GetFileName(png)} failed: {ex.Message}");
                    TryDelete(jpg);
                    existingJpgs.Remove(jpgName);
                    before += pngBytes;
                    after += pngBytes;
                    failed++;
                }
            }

            // ---- phase 2: what should be referenced now, from what is ON DISK -----------------
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string jpgName in existingJpgs)
            {
                string pngName = Path.ChangeExtension(jpgName, ".png");
                renames[Relative(pngName)] = Relative(jpgName);
            }

            int repaired = existingJpgs.Count - converted;

            // ---- phase 3: repoint, before anything is deleted ---------------------------------
            if (renames.Count > 0)
            {
                UpdateManifest(dir, renames);
                UpdateWalkthrough(dir, renames);
            }

            // ---- phase 4: now the PNGs are safe to remove ------------------------------------
            foreach (string jpgName in existingJpgs)
            {
                string png = Path.Combine(shots, Path.ChangeExtension(jpgName, ".png"));
                if (File.Exists(png)) TryDelete(png);
            }

            Log.Info($"[FrameConversion] {Path.GetFileName(dir)}: {converted} converted, "
                   + $"{Math.Max(0, repaired)} finished from an earlier pass, {failed} failed, "
                   + $"{before:N0} -> {after:N0} bytes");

            return new FrameConversionResult
            {
                Converted = converted,
                Repaired = Math.Max(0, repaired),
                Failed = failed,
                BytesBefore = before,
                BytesAfter = after,
            };
        }

        /// <summary>
        /// Encode one PNG as JPEG, in this process.
        ///
        /// In-process rather than through ffmpeg, and the difference is not marginal: a separate ffmpeg
        /// per frame managed two frames a second, which is 108 minutes for one library's 13,000 eligible
        /// frames - almost all of it process start-up rather than encoding. The same work through
        /// System.Drawing is hundreds of frames a second, and Screenshot.cs already writes images this
        /// way, so it is the same dependency rather than a new one.
        /// </summary>
        private static void EncodeJpeg(string pngPath, string jpgPath, int quality)
        {
            quality = Math.Clamp(quality, 1, 100);

            var codec = Imaging.ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == Imaging.ImageFormat.Jpeg.Guid)
                ?? throw new InvalidOperationException("this machine has no JPEG encoder");

            using var parameters = new Imaging.EncoderParameters(1);
            using var parameter = new Imaging.EncoderParameter(Imaging.Encoder.Quality, (long)quality);
            parameters.Param[0] = parameter;

            using var image = Drawing.Image.FromFile(pngPath);
            image.Save(jpgPath, codec, parameters);
        }

        private static string Relative(string fileName) =>
            HousekeepingPlan.FramesDirectory + "/" + fileName;

        /// <summary>
        /// Repoint the manifest's shot list at the converted files.
        ///
        /// Read-modify-write through <see cref="ManifestStore"/>, once for the whole set, and matching on
        /// BOTH slash spellings because Package has written these entries with a backslash on this
        /// platform before now (issue #155's unknown-property bag exists for the same class of reason).
        /// </summary>
        private static void UpdateManifest(string dir, IReadOnlyDictionary<string, string> renames)
        {
            ManifestStore.Update(dir, m =>
            {
                foreach (var shot in m.Shots)
                {
                    string normalized = shot.File.Replace('\\', '/');
                    if (renames.TryGetValue(normalized, out string? to)) shot.File = to;
                }
                for (int i = 0; i < m.Files.Count; i++)
                {
                    string normalized = m.Files[i].Replace('\\', '/');
                    if (renames.TryGetValue(normalized, out string? to)) m.Files[i] = to;
                }
            });
        }

        /// <summary>
        /// Repoint walkthrough.html at the converted files.
        ///
        /// A plain textual substitution of one relative path for another, written through a temporary
        /// file and a rename. It is deliberately NOT an HTML edit: nothing is inserted, nothing removed,
        /// no element restructured, so the page cannot come out malformed. That restraint is why this
        /// slice converts frames and does not also DROP redundant ones - dropping a frame means taking
        /// its block out of the page, and rebuilding the page is a different job from renaming a file
        /// inside it.
        ///
        /// A missing page is not an error: a recording can be packaged far enough to have frames without
        /// having a walkthrough.
        /// </summary>
        private static void UpdateWalkthrough(string dir, IReadOnlyDictionary<string, string> renames)
        {
            string page = Path.Combine(dir, WalkthroughFile);
            if (!File.Exists(page))
            {
                Log.Info($"[FrameConversion] {Path.GetFileName(dir)}: no {WalkthroughFile} to repoint");
                return;
            }

            string html = File.ReadAllText(page);
            string updated = Rewrite(html, renames);
            if (updated == html)
            {
                Log.Info($"[FrameConversion] {Path.GetFileName(dir)}: {WalkthroughFile} already current");
                return;
            }

            string temp = page + ".housekeeping.tmp";
            File.WriteAllText(temp, updated);
            File.Move(temp, page, overwrite: true);
            Log.Info($"[FrameConversion] {Path.GetFileName(dir)}: {WalkthroughFile} repointed");
        }

        /// <summary>
        /// The substitution itself, separated so it can be tested on a string without a directory.
        /// Both slash spellings are replaced, for the same reason the manifest matches both.
        /// </summary>
        internal static string Rewrite(string html, IReadOnlyDictionary<string, string> renames)
        {
            foreach (var pair in renames)
            {
                html = html.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
                html = html.Replace(pair.Key.Replace('/', '\\'), pair.Value, StringComparison.Ordinal);
            }
            return html;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Warn($"[FrameConversion] could not remove {path}: {ex.Message}");
            }
        }
    }
}
