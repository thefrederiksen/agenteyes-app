using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.DevThrottle;

namespace AgentEyes.Packaging
{
    /// <summary>
    /// Names a recording that was transcribed but never titled (issue #138).
    ///
    /// Titling is non-fatal by design: a naming failure must not cost the transcript. The gap was
    /// that nothing ever retried it, so a single stalled request left the recording showing its
    /// generic preset name ("Monitor 1") permanently, with no way back short of re-running the whole
    /// package pass.
    /// </summary>
    internal static class TitleBackfill
    {
        /// <summary>
        /// Generates and saves a title for an already-transcribed recording. Returns true when a
        /// title was written. The attempt is recorded FIRST so a request that hangs or crashes still
        /// consumes one of the recording's tries.
        /// </summary>
        public static async Task<bool> TitleAsync(string dir, CancellationToken ct = default)
        {
            Log.Info($"[TitleBackfill] TitleAsync: dir={dir}");
            TranscriptionBacklog.NoteTitleAttempt(dir);

            var segments = ReadSegments(dir);
            if (segments.Count == 0)
            {
                Log.Info("[TitleBackfill] TitleAsync: transcript has no segments; nothing to title");
                return false;
            }

            var named = await TitleGenerator.GenerateAsync(segments);
            Apply(dir, named);

            Log.Info($"[TitleBackfill] TitleAsync: titled '{named.Title}'");
            return true;
        }

        /// <summary>
        /// Write a generated title into the recording's manifest.
        ///
        /// Separate from <see cref="TitleAsync"/> so the write can be exercised without a network
        /// round trip - the generation is the only part that needs one, and the write is the part
        /// that has twice been the defect (issue #155).
        ///
        /// Two rules, both of them the issue #155 fix:
        ///  - the title is applied to the manifest as it reads NOW, because generating it took a
        ///    round trip and the recording may have been renamed meanwhile;
        ///  - the AI cost is ADDED to the recording's running total rather than assigned over it. A
        ///    recording whose title generation failed, that was then translated, and that this pass
        ///    later titles, must not lose the translation's usage.
        /// </summary>
        internal static Manifest Apply(string dir, TitleGenerator.TitleResult named)
        {
            if (named == null) throw new ArgumentNullException(nameof(named));

            Log.Info($"[TitleBackfill] Apply: dir={dir}, title='{named.Title}'");
            return ManifestStore.Update(dir, manifest =>
            {
                manifest.Title = named.Title;
                manifest.Description = named.Description;
                manifest.AiCost = Ai.AiCostLedger.Add(manifest.AiCost, named.Usage, named.Model);
            });
        }

        /// <summary>Reads the segments written by <see cref="Package.WriteTranscript"/>.</summary>
        private static IReadOnlyList<TranscriptSegment> ReadSegments(string dir)
        {
            string path = Path.Combine(dir, "transcript.json");
            if (!File.Exists(path)) return Array.Empty<TranscriptSegment>();

            var segments = JsonSerializer.Deserialize<List<TranscriptSegment>>(File.ReadAllText(path));
            return segments ?? (IReadOnlyList<TranscriptSegment>)Array.Empty<TranscriptSegment>();
        }
    }
}
