using System.Collections.Generic;
using AgentEyes.Transcription;
using AgentEyes.Packaging;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The dictionary must correct recording transcripts:
    /// known misheard forms are replaced deterministically after Whisper.
    /// </summary>
    public class TranscriptDictionaryTests
    {
        private static TranscriptionDictionary Dict(params (string Term, string[] Variants)[] entries)
        {
            var map = new Dictionary<string, IReadOnlyList<string>>();
            var vocab = new List<string>();
            foreach (var (term, variants) in entries)
            {
                vocab.Add(term);
                map[term] = variants;
            }
            return new TranscriptionDictionary(vocab, map);
        }

        [Fact]
        public void Replaces_KnownVariant_WithTerm()
        {
            var dict = Dict(("mindzie", new[] { "Minzy", "Mindsy" }));
            Assert.Equal("We use mindzie at work.",
                TranscriptDictionary.Apply("We use Minzy at work.", dict));
        }

        [Fact]
        public void Replaces_MultiWordVariant()
        {
            var dict = Dict(("mindzie", new[] { "Mind Seeds" }));
            Assert.Equal("The mindzie dashboard is open.",
                TranscriptDictionary.Apply("The Mind Seeds dashboard is open.", dict));
        }

        [Fact]
        public void IsCaseSensitive_DistinctWrongForms()
        {
            var dict = Dict(("cc-director", new[] { "CC Director" }));
            // Only the exact recorded wrong form is replaced.
            Assert.Equal("cc-director and cc director",
                TranscriptDictionary.Apply("CC Director and cc director", dict));
        }

        [Fact]
        public void RespectsWordBoundaries()
        {
            var dict = Dict(("mindzie", new[] { "Minzy" }));
            Assert.Equal("Minzyish stays untouched.",
                TranscriptDictionary.Apply("Minzyish stays untouched.", dict));
        }

        [Fact]
        public void Segments_AreCorrectedInPlace_AndCounted()
        {
            var dict = Dict(("Tailscale", new[] { "Tail Scale", "Terascale" }));
            var segments = new List<TranscriptSegment>
            {
                new() { Text = "Connect over Tail Scale first." },
                new() { Text = "Then Terascale routes it. Tail Scale again." },
                new() { Text = "Nothing to fix here." },
            };
            int fixes = TranscriptDictionary.Apply(segments, dict);
            Assert.Equal(3, fixes);
            Assert.Equal("Connect over Tailscale first.", segments[0].Text);
            Assert.Equal("Then Tailscale routes it. Tailscale again.", segments[1].Text);
            Assert.Equal("Nothing to fix here.", segments[2].Text);
        }

        [Fact]
        public void EmptyDictionary_LeavesTextAlone()
        {
            Assert.Equal("Hello Minzy.",
                TranscriptDictionary.Apply("Hello Minzy.", TranscriptionDictionary.Empty));
        }

        [Fact]
        public void TermContainingDollar_IsInsertedLiterally()
        {
            var dict = Dict(("A$B", new[] { "ab" }));
            Assert.Equal("see A$B now",
                TranscriptDictionary.Apply("see ab now", dict));
        }
    }
}
