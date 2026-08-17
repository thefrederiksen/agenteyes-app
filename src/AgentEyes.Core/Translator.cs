using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentEyes.Ai;
using AgentEyes.DevThrottle;
using AgentEyes.Packaging;

namespace AgentEyes
{
    /// <summary>
    /// Issue #101: translate an existing recording's transcript into another language while PRESERVING
    /// cue timing, and store the result as a new per-language WebVTT beside the original so one
    /// recording can carry several languages.
    ///
    /// The engine (in order): resolves the recording folder, reads the SOURCE transcript segments (the
    /// subtitle-ready transcript.&lt;src&gt;.vtt written by issue #98, else transcript.json), translates
    /// each cue's TEXT via the existing DevThrottle AI provider already used for titles/descriptions
    /// (issue #88 - NO new provider), keeps every cue's start/end UNCHANGED, writes
    /// transcript.&lt;target&gt;.vtt, and registers the new language in the manifest per-language map
    /// (issue #98). Cost is accumulated onto the recording's <see cref="Manifest.AiCost"/> the same way
    /// titling records it (from the API token usage).
    ///
    /// Cue boundaries are preserved 1:1 (issue #101 assumption A2, the key risk): cues are translated in
    /// small index-aligned batches and each batch's response is re-split back to that batch's exact cue
    /// count. A batch that comes back with the wrong count is a hard error (no cue re-timing, no partial
    /// VTT). All translated cues are assembled IN MEMORY and the VTT is written only after every batch
    /// succeeds, so an AI failure leaves no half-written transcript behind (AC4).
    ///
    /// The pure, network-free pieces (<see cref="ResolveDir"/>, <see cref="ReadSourceSegments"/>,
    /// <see cref="ApplyTranslations"/>, <see cref="BuildRequestBody"/>, <see cref="ParseResponse"/>,
    /// <see cref="WriteTranslatedVtt"/>, <see cref="AccumulateCost"/>) are split out so the cue-preserving
    /// mapping and guards are unit-testable with the AI call mocked - no HTTP. <see cref="RunAsync"/> is
    /// the orchestrator that wires in the real hosted chat call.
    /// </summary>
    internal static class Translator
    {
        /// <summary>The chat model used for translation - the same DevThrottle model titling uses (AC5).</summary>
        public static string Model => DevThrottleClient.ChatModel;

        /// <summary>How many cues are sent per chat request. Small batches keep the model's index
        /// alignment reliable so each response re-splits cleanly back to its source cue count (A2).</summary>
        internal const int CuesPerBatch = 25;

        /// <summary>Common language-code to English-name hints for the translation prompt (assumption
        /// A1). An unlisted but well-formed code is passed through as-is - the hosted model interprets
        /// the code - so this map is only a prompt hint, never a whitelist that would reject a language.</summary>
        private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "English", ["tr"] = "Turkish", ["es"] = "Spanish", ["fr"] = "French",
            ["de"] = "German", ["it"] = "Italian", ["pt"] = "Portuguese", ["nl"] = "Dutch",
            ["da"] = "Danish", ["sv"] = "Swedish", ["no"] = "Norwegian", ["fi"] = "Finnish",
            ["pl"] = "Polish", ["ru"] = "Russian", ["uk"] = "Ukrainian", ["ar"] = "Arabic",
            ["hi"] = "Hindi", ["ja"] = "Japanese", ["ko"] = "Korean", ["zh"] = "Chinese",
        };

        /// <summary>The outcome of a translate run: the recording id, its folder, the target language,
        /// and how many cues were written.</summary>
        internal sealed record TranslateResult(string Id, string Dir, string Language, int CueCount);

        /// <summary>Synchronous entry point for the CLI (<c>agenteyes translate &lt;id&gt; --to &lt;lang&gt;</c>).</summary>
        public static TranslateResult Run(string idOrPath, string targetLanguage, string? root = null) =>
            RunAsync(idOrPath, targetLanguage, root).GetAwaiter().GetResult();

        /// <summary>
        /// Orchestrate a full translate: resolve the folder -> read source cues -> translate cue text in
        /// index-aligned batches (cost accumulated) -> preserve every cue's timing -> write
        /// transcript.&lt;lang&gt;.vtt and register the language in the manifest. A failure at any step
        /// surfaces the exact reason (no silent fallback) and, because the VTT is written last, leaves no
        /// partial artifact.
        /// </summary>
        internal static async Task<TranslateResult> RunAsync(string idOrPath, string targetLanguage, string? root = null, CancellationToken ct = default)
        {
            Log.Info($"[Translator] RunAsync: idOrPath={idOrPath}, to={targetLanguage}");

            string lang = NormalizeLanguage(targetLanguage);
            string dir = ResolveDir(idOrPath, root);
            string id = Path.GetFileName(dir);
            var manifest = Manifest.Load(dir);

            var source = ReadSourceSegments(dir, manifest, lang, out string sourceLanguage);
            Console.WriteLine($"[ok] translating {source.Count} cue(s) {sourceLanguage} -> {lang} ({Model})");

            if (!DevThrottleAccount.IsSignedIn)
                throw new UsageException("Not signed in to DevThrottle - cannot translate. Open Settings > DevThrottle Account and sign in.");

            // Translate cue text in small index-aligned batches; keep every batch's response re-split to
            // its own cue count and accumulate into one full translated-text list (A2). Nothing is
            // written to disk until every batch has succeeded (AC4 - no partial VTT).
            var translatedText = new List<string>(source.Count);
            AiUsage? totalUsage = null;
            try
            {
                for (int start = 0; start < source.Count; start += CuesPerBatch)
                {
                    var batch = source.Skip(start).Take(CuesPerBatch).Select(s => s.Text ?? string.Empty).ToList();
                    var (texts, usage) = await TranslateBatchAsync(batch, lang, ct);
                    translatedText.AddRange(texts);
                    totalUsage = AddUsage(totalUsage, usage);
                }
            }
            catch (DevThrottleException dex)
            {
                Console.WriteLine($"  translation FAILED: {dex.Message}");
                throw new UsageException(dex.Message, dex);
            }

            var translatedSegments = ApplyTranslations(source, translatedText);

            WriteTranslatedVtt(dir, lang, translatedSegments, totalUsage);

            Console.WriteLine($"[ok] wrote {translatedSegments.Count} cue(s) -> {WebVtt.FileNameFor(lang)}");
            Log.Info($"[Translator] RunAsync: done id={id}, lang={lang}, cues={translatedSegments.Count}");
            return new TranslateResult(id, dir, lang, translatedSegments.Count);
        }

        // ---- resolution + source reading --------------------------------------

        /// <summary>
        /// Map an id (a recording session-directory leaf under the recordings root) OR a direct folder
        /// path to its absolute directory. Rejects path separators / traversal on a bare id so it can
        /// never escape the root. Throws <see cref="UsageException"/> (non-zero CLI exit) when no such
        /// recording exists. Pure and side-effect free.
        /// </summary>
        internal static string ResolveDir(string idOrPath, string? root)
        {
            if (string.IsNullOrWhiteSpace(idOrPath))
                throw new UsageException("translate needs a recording id or folder: agenteyes translate <id> --to <lang>");

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
        /// Read the SOURCE transcript segments to translate (issue #101). Prefers the subtitle-ready
        /// transcript.&lt;src&gt;.vtt (issue #98) - English when present, else any existing language other
        /// than the target - and falls back to the timed transcript.json. Both are legitimate cue sources
        /// (this mirrors <see cref="RecordingLibrary.GetTranscript"/>). Throws
        /// <see cref="UsageException"/> when the recording has NO transcript at all (AC4). Pure and
        /// side-effect free. <paramref name="sourceLanguage"/> reports which language was read.
        /// </summary>
        internal static List<TranscriptSegment> ReadSourceSegments(string dir, Manifest manifest, string targetLanguage, out string sourceLanguage)
        {
            // Choose the source language from the per-language map: English first, else the first
            // registered language that is not the target we are producing.
            string? sourceVttName = null;
            sourceLanguage = WebVtt.DefaultLanguage;
            if (manifest.Transcripts.TryGetValue(WebVtt.DefaultLanguage, out var enName))
            {
                sourceVttName = enName;
                sourceLanguage = WebVtt.DefaultLanguage;
            }
            else
            {
                foreach (var kvp in manifest.Transcripts.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (string.Equals(kvp.Key, targetLanguage, StringComparison.OrdinalIgnoreCase)) continue;
                    sourceVttName = kvp.Value;
                    sourceLanguage = kvp.Key;
                    break;
                }
            }

            if (sourceVttName != null)
            {
                string vttPath = Path.Combine(dir, sourceVttName);
                if (File.Exists(vttPath))
                {
                    var segs = WebVtt.Read(File.ReadAllText(vttPath));
                    if (segs.Count > 0) return segs;
                }
            }

            // Fall back to the timed transcript.json (the same segments the VTT is derived from).
            string jsonName = string.IsNullOrWhiteSpace(manifest.Transcript) ? "transcript.json" : manifest.Transcript!;
            string jsonPath = Path.Combine(dir, jsonName);
            if (File.Exists(jsonPath))
            {
                var segs = JsonSerializer.Deserialize<List<TranscriptSegment>>(File.ReadAllText(jsonPath));
                if (segs is { Count: > 0 })
                {
                    sourceLanguage = WebVtt.DefaultLanguage;
                    return segs;
                }
            }

            throw new UsageException(
                $"recording '{Path.GetFileName(dir)}' has no transcript to translate. Package or import it first so it has a transcript.");
        }

        // ---- pure cue-preserving mapping (AC1) --------------------------------

        /// <summary>
        /// Zip the translated cue TEXT back onto the SOURCE segments, keeping each cue's start/end
        /// UNCHANGED (issue #101, AC1). The two lists must be the same length - a mismatch means the
        /// translation dropped, merged, or added cues and cue timing could not be preserved, so this
        /// throws rather than guess (A2; guarantees the caller writes no re-timed / partial VTT). Pure.
        /// </summary>
        internal static List<TranscriptSegment> ApplyTranslations(IReadOnlyList<TranscriptSegment> source, IReadOnlyList<string> translated)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (translated is null) throw new ArgumentNullException(nameof(translated));
            if (translated.Count != source.Count)
                throw new UsageException(
                    $"translation returned {translated.Count} cue(s) for {source.Count} source cue(s) - cannot preserve cue timing.");

            var result = new List<TranscriptSegment>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                result.Add(new TranscriptSegment
                {
                    StartSeconds = source[i].StartSeconds,
                    EndSeconds = source[i].EndSeconds,
                    Text = translated[i] ?? string.Empty,
                });
            }
            return result;
        }

        // ---- AI request / response (mockable, same shape as TitleGenerator) ---

        /// <summary>
        /// Build the chat-completions request body for one batch of cues (AC5 - same DevThrottle chat
        /// provider titling uses). The cues are sent as a JSON array of strings and the model is asked
        /// for a JSON array of the SAME length, same order, so the response re-splits 1:1 back onto the
        /// source cues. Pure.
        /// </summary>
        internal static string BuildRequestBody(IReadOnlyList<string> cueTexts, string targetLanguage, string model)
        {
            string name = LanguageNames.TryGetValue(targetLanguage, out var n) ? n : targetLanguage;
            string system =
                $"You are a subtitle translator. You are given a JSON array of caption strings in some source language. "
                + $"Translate EACH element into {name} (language code '{targetLanguage}'). "
                + "Reply with ONLY a JSON array of strings - the translations - with EXACTLY the same number of elements "
                + "and in the SAME order as the input. Do not merge, split, add, drop, renumber, or reorder elements; "
                + "the i-th output is the translation of the i-th input. Preserve any bracketed non-speech markers "
                + "(for example [BLANK_AUDIO]) unchanged. No prose, no code fences, no keys - just the JSON array.";
            return JsonSerializer.Serialize(new
            {
                model,
                temperature = 0.2,
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = JsonSerializer.Serialize(cueTexts) },
                },
            });
        }

        /// <summary>
        /// Parse a chat response body into the batch's translated strings. Unwraps the assistant
        /// message content, strips a ```json fence if present, and reads the JSON array. Throws
        /// <see cref="UsageException"/> when the body is not the expected shape or does not carry
        /// <paramref name="expectedCount"/> elements (A2 re-split guard). Pure.
        /// </summary>
        internal static List<string> ParseResponse(string responseJson, int expectedCount)
        {
            string content;
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                content = doc.RootElement.GetProperty("choices")[0]
                    .GetProperty("message").GetProperty("content").GetString() ?? "";
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
            {
                throw new UsageException($"unexpected translation response: {Snippet(responseJson)}");
            }

            var result = new List<string>(expectedCount);
            try
            {
                using var inner = JsonDocument.Parse(TitleGenerator.StripFences(content));
                if (inner.RootElement.ValueKind != JsonValueKind.Array)
                    throw new UsageException($"translation response is not a JSON array: {Snippet(content)}");
                foreach (var el in inner.RootElement.EnumerateArray())
                    result.Add(el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : el.ToString());
            }
            catch (JsonException)
            {
                throw new UsageException($"translation response is not valid JSON: {Snippet(content)}");
            }

            if (result.Count != expectedCount)
                throw new UsageException(
                    $"translation returned {result.Count} item(s) for a {expectedCount}-cue batch - cannot preserve cue timing.");
            return result;
        }

        // ---- write + cost -----------------------------------------------------

        /// <summary>
        /// Write the translated cues as transcript.&lt;lang&gt;.vtt (reusing the issue #98 <see cref="WebVtt"/>
        /// writer) and register the language in the manifest per-language map (issue #98). Overwriting an
        /// EXISTING language refreshes that one file and its single map entry rather than duplicating (AC3 -
        /// the dictionary set and <see cref="File.WriteAllText"/> are both idempotent).
        ///
        /// Issue #155: the manifest is read, changed and written in one <see cref="ManifestStore.Update"/>,
        /// so the registration - and the accumulated AI cost, which is a read-modify-write of its own -
        /// apply to what the manifest says NOW. Translating takes several network round trips; the copy
        /// loaded before them is stale by the time it finishes.
        /// </summary>
        internal static Manifest WriteTranslatedVtt(
            string dir, string language, IReadOnlyList<TranscriptSegment> translatedSegments, AiUsage? usage)
        {
            string vttName = WebVtt.FileNameFor(language);
            File.WriteAllText(Path.Combine(dir, vttName), WebVtt.Write(translatedSegments));
            return ManifestStore.Update(dir, m =>
            {
                m.Transcripts[language] = vttName;
                if (!m.Files.Contains(vttName)) m.Files.Add(vttName);
                // Issue #155: every producer of AiCost accumulates through the one ledger, so no
                // path can erase what another already recorded.
                m.AiCost = AiCostLedger.Add(m.AiCost, usage, Model);
            });
        }

        // ---- internals --------------------------------------------------------

        /// <summary>Translate one batch of cues through the DevThrottle chat provider, returning the
        /// index-aligned translations and the call's token usage (null when the provider omitted it).</summary>
        private static async Task<(List<string> Texts, AiUsage? Usage)> TranslateBatchAsync(IReadOnlyList<string> cueTexts, string targetLanguage, CancellationToken ct)
        {
            // A translation batch generates far more output than a one-line title, so it keeps a
            // generous budget rather than the short chat default (issue #138).
            var (status, body) = await DevThrottleClient.PostChatAsync(
                BuildRequestBody(cueTexts, targetLanguage, Model), ct, TimeSpan.FromMinutes(5));
            if (status is < 200 or >= 300)
                throw DevThrottleClient.ErrorFrom(status, body);
            return (ParseResponse(body, cueTexts.Count), TitleGenerator.ParseUsage(body));
        }

        /// <summary>Validate + normalize the target language code (assumption A1). A well-formed short
        /// code (letters, optionally a region suffix like <c>pt-BR</c>) is accepted; garbage is rejected
        /// with a clear error rather than sent to the model.</summary>
        internal static string NormalizeLanguage(string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(targetLanguage))
                throw new UsageException("translate needs a target language: agenteyes translate <id> --to <lang> (e.g. --to tr).");

            string lang = targetLanguage.Trim();
            foreach (char c in lang)
            {
                if (!char.IsLetter(c) && c != '-')
                    throw new UsageException($"'{targetLanguage}' is not a valid language code. Use a short code such as 'tr', 'es', or 'pt-BR'.");
            }
            return lang.ToLowerInvariant();
        }

        private static AiUsage? AddUsage(AiUsage? a, AiUsage? b)
        {
            if (a is null) return b;
            if (b is null) return a;
            return new AiUsage(a.PromptTokens + b.PromptTokens, a.CompletionTokens + b.CompletionTokens);
        }

        private static string Snippet(string text)
        {
            string oneLine = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return oneLine.Length <= 200 ? oneLine : oneLine[..200] + "...";
        }
    }
}
