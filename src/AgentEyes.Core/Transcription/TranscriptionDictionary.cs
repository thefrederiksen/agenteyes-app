using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AgentEyes.Transcription
{
    /// <summary>
    /// User-editable transcription dictionary: the speaker's canonical terms plus the
    /// wrong forms Whisper has produced for them. This is the CLOSED SET of
    /// permitted rewrites for the cleanup pass (issue #10) - the cleanup model can
    /// propose rewriting a span TO nothing except these terms.
    /// </summary>
    internal sealed record TranscriptionDictionary(
        IReadOnlyList<string> Vocabulary,
        IReadOnlyDictionary<string, IReadOnlyList<string>> CommonMistranscriptions)
    {
        public static TranscriptionDictionary Empty { get; } = new(
            Array.Empty<string>(),
            new Dictionary<string, IReadOnlyList<string>>());

        public bool IsEmpty => Vocabulary.Count == 0 && CommonMistranscriptions.Count == 0;

        /// <summary>
        /// A copy with <paramref name="misheard"/> recorded as a wrong form of
        /// <paramref name="term"/>, adding the term itself when it is new. Casing
        /// matters for wrong forms ("CC Director" and "CC director" are distinct),
        /// so the duplicate check is ordinal. Already-known pairs return this
        /// instance unchanged.
        /// </summary>
        public TranscriptionDictionary WithPair(string term, string misheard)
        {
            term = term.Trim();
            misheard = misheard.Trim();
            if (term.Length == 0 || misheard.Length == 0) return this;

            var vocabulary = Vocabulary;
            if (!Vocabulary.Contains(term, StringComparer.Ordinal)
                && !CommonMistranscriptions.ContainsKey(term))
                vocabulary = Vocabulary.Append(term).ToList();

            var variants = CommonMistranscriptions.TryGetValue(term, out var existing)
                ? existing
                : Array.Empty<string>();
            if (variants.Contains(misheard, StringComparer.Ordinal))
                return vocabulary == Vocabulary ? this : new TranscriptionDictionary(vocabulary, CommonMistranscriptions);

            var patterns = CommonMistranscriptions.ToDictionary(
                kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            patterns[term] = variants.Append(misheard).ToList();
            return new TranscriptionDictionary(vocabulary, patterns);
        }
    }

    /// <summary>
    /// Loads %LOCALAPPDATA%\AgentEyes\dictionary.json, seeding a starter
    /// dictionary on first use so cleanup is useful out of the box. The file is
    /// meant to be hand-edited:
    ///
    ///   {
    ///     "Vocabulary": ["mindzie", "cc-director"],
    ///     "CommonMistranscriptions": { "mindzie": ["Minzy", "Mindsy"] }
    ///   }
    ///
    /// An unreadable file loads as empty (cleanup then no-ops for that take) with
    /// a loud log line - transcription itself must never break on a dictionary typo.
    /// </summary>
    internal static class DictionaryStore
    {
        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentEyes", "dictionary.json");

        public static TranscriptionDictionary Load() => Load(DefaultPath);

        public static TranscriptionDictionary Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Save(path, Seed);
                    Log.Info($"dictionary: seeded starter file at {path}");
                    return Seed;
                }
                var shape = JsonSerializer.Deserialize<Shape>(File.ReadAllText(path));
                return shape == null ? TranscriptionDictionary.Empty : FromShape(shape);
            }
            catch (Exception ex)
            {
                Log.Warn($"dictionary: {path} is unreadable ({ex.Message}); cleanup will no-op until it is fixed");
                return TranscriptionDictionary.Empty;
            }
        }

        public static void Save(string path, TranscriptionDictionary dictionary)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var shape = new Shape
            {
                Vocabulary = dictionary.Vocabulary.ToList(),
                CommonMistranscriptions = dictionary.CommonMistranscriptions
                    .ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(shape, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static TranscriptionDictionary FromShape(Shape shape)
        {
            var vocab = (shape.Vocabulary ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToList();

            var patterns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var kv in shape.CommonMistranscriptions ?? new Dictionary<string, List<string>>())
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
                var variants = kv.Value.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
                if (variants.Count > 0) patterns[kv.Key.Trim()] = variants;
            }

            return new TranscriptionDictionary(vocab, patterns);
        }

        /// <summary>Starter terms + wrong forms actually observed in cc-director's production logs.</summary>
        internal static readonly TranscriptionDictionary Seed = new(
            new[] { "mindzie", "cc-director", "AgentEyes", "CenCon", "Tailscale", "Soren Frederiksen" },
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["mindzie"] = new[] { "Minzy", "Mindsy", "Mindzy", "Mind Seeds", "mindseeds" },
                ["cc-director"] = new[] { "CC Director", "See Director", "CC director" },
                ["AgentEyes"] = new[] { "My Quiet Shadow", "my quiet shadow" },
                ["CenCon"] = new[] { "SenCon", "SENCON", "Sencon" },
                ["Tailscale"] = new[] { "Teraskale", "Terascale", "Tail Scale" },
                ["Soren Frederiksen"] = new[] { "Soren Fredriksen", "Soeren Frederiksen" },
            });

        private sealed class Shape
        {
            public List<string>? Vocabulary { get; set; }
            public Dictionary<string, List<string>>? CommonMistranscriptions { get; set; }
        }
    }
}
