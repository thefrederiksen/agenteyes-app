using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AgentEyes.Transcription;

namespace AgentEyes.Packaging
{
    /// <summary>
    /// Applies the user dictionary to recording transcripts: every known misheard
    /// form (CommonMistranscriptions) is replaced with its canonical term. This is
    /// DETERMINISTIC find/replace of the closed set the user maintains - no AI
    /// involved, so it works offline, costs nothing, and can never rewrite
    /// anything except the exact wrong forms the user has recorded.
    ///
    /// Runs right after Whisper inside the packaging pipeline, before the
    /// transcript files are written and before title generation - so transcripts,
    /// walkthroughs AND generated titles all benefit.
    /// </summary>
    internal static class TranscriptDictionary
    {
        /// <summary>Replace known misheard forms in all segments, in place.
        /// Returns the number of replacements made (for the log).</summary>
        public static int Apply(IList<TranscriptSegment> segments, TranscriptionDictionary dictionary) =>
            new DictionaryReplacer(dictionary).Apply(segments);

        /// <summary>Replace known misheard forms in one string (exposed for tests
        /// and any other text consumer).</summary>
        public static string Apply(string text, TranscriptionDictionary dictionary) =>
            new DictionaryReplacer(dictionary).Apply(text);

        internal static List<(Regex Pattern, string Term)> BuildRules(TranscriptionDictionary dictionary)
        {
            // Longest variants first so "Mind Seeds" wins before any shorter overlap.
            var rules = new List<(Regex, string)>();
            foreach (var (term, variants) in dictionary.CommonMistranscriptions)
            {
                foreach (var variant in variants)
                {
                    if (string.IsNullOrWhiteSpace(variant) || variant == term) continue;
                    // Word boundaries where the variant starts/ends with word characters,
                    // so "Minzy" never matches inside "Minzyish". Case-sensitive: the
                    // dictionary records the exact wrong forms.
                    string pattern =
                        (char.IsLetterOrDigit(variant[0]) ? @"\b" : "")
                        + Regex.Escape(variant)
                        + (char.IsLetterOrDigit(variant[^1]) ? @"\b" : "");
                    rules.Add((new Regex(pattern, RegexOptions.CultureInvariant), term));
                }
            }
            rules.Sort((a, b) => b.Item1.ToString().Length.CompareTo(a.Item1.ToString().Length));
            return rules;
        }

        internal static string ApplyRules(string text, List<(Regex Pattern, string Term)> rules, ref int total)
        {
            foreach (var (pattern, term) in rules)
            {
                int hits = pattern.Matches(text).Count;
                if (hits == 0) continue;
                text = pattern.Replace(text, term.Replace("$", "$$"));
                total += hits;
            }
            return text;
        }
    }

    /// <summary>
    /// The dictionary's replacement rules compiled ONCE for repeated use, so a
    /// pass over many segments does not rebuild the regexes per segment. Same
    /// semantics as the static TranscriptDictionary.Apply.
    /// </summary>
    internal sealed class DictionaryReplacer
    {
        private readonly List<(Regex Pattern, string Term)> _rules;

        public DictionaryReplacer(TranscriptionDictionary dictionary) =>
            _rules = TranscriptDictionary.BuildRules(dictionary);

        public bool IsEmpty => _rules.Count == 0;

        public string Apply(string text)
        {
            if (_rules.Count == 0 || string.IsNullOrEmpty(text)) return text;
            int unused = 0;
            return TranscriptDictionary.ApplyRules(text, _rules, ref unused);
        }

        public int Apply(IList<TranscriptSegment> segments)
        {
            int total = 0;
            if (_rules.Count == 0) return 0;
            foreach (var segment in segments)
                segment.Text = TranscriptDictionary.ApplyRules(segment.Text, _rules, ref total);
            return total;
        }
    }
}
