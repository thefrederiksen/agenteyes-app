using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes;
using AgentEyes.Ai;
using AgentEyes.Packaging;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The transcript translation engine (issue #101). Covers the pure, network-free pieces with the AI
    /// call mocked by supplying its JSON directly:
    /// (AC1) the cue-preserving mapping keeps cue count + timings and only replaces text, and a
    ///       count-mismatch is a hard error (assumption A2 re-split guard);
    /// (AC2) writing registers the new language in the manifest per-language map and RecordingLibrary
    ///       reports the recording now has that language;
    /// (AC3) translating a language that already exists overwrites/refreshes it (no duplicate);
    /// (AC4) a recording with no transcript, or an unknown id, produces a clear error and no partial VTT;
    /// (AC5) the AI request targets the same DevThrottle chat model titling uses and cost is accumulated
    ///       the same way (from token usage).
    /// </summary>
    public class TranslatorTests : IDisposable
    {
        private readonly string _root;

        public TranslatorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-translate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- AC1: cue-preserving mapping --------------------------------------

        [Fact]
        public void ApplyTranslations_PreservesCountAndTimings_ReplacesTextOnly()
        {
            var source = new List<TranscriptSegment>
            {
                new() { StartSeconds = 0.0, EndSeconds = 2.0,  Text = "hello" },
                new() { StartSeconds = 2.0, EndSeconds = 5.5,  Text = "how are you" },
                new() { StartSeconds = 5.5, EndSeconds = 9.25, Text = "goodbye" },
            };
            var translated = new[] { "merhaba", "nasilsin", "hoscakal" };

            var result = Translator.ApplyTranslations(source, translated);

            Assert.Equal(source.Count, result.Count);                 // same number of cues
            for (int i = 0; i < source.Count; i++)
            {
                Assert.Equal(source[i].StartSeconds, result[i].StartSeconds);  // timing unchanged
                Assert.Equal(source[i].EndSeconds, result[i].EndSeconds);
                Assert.Equal(translated[i], result[i].Text);                   // only text replaced
            }
        }

        [Fact]
        public void ApplyTranslations_CountMismatch_Throws()
        {
            var source = new List<TranscriptSegment>
            {
                new() { StartSeconds = 0.0, EndSeconds = 2.0, Text = "one" },
                new() { StartSeconds = 2.0, EndSeconds = 4.0, Text = "two" },
            };
            // Model merged two cues into one - cue timing can no longer be preserved.
            var ex = Assert.Throws<UsageException>(() => Translator.ApplyTranslations(source, new[] { "birbuzuk" }));
            Assert.Contains("cannot preserve cue timing", ex.Message);
        }

        [Fact]
        public void ApplyTranslations_ThenWrite_ProducesVttWithIdenticalTimings()
        {
            string dir = MakeRecording("2026-07-10_140000_timing");
            WriteSourceVtt(dir, new List<TranscriptSegment>
            {
                new() { StartSeconds = 0.0,  EndSeconds = 3.25, Text = "first" },
                new() { StartSeconds = 3.25, EndSeconds = 7.0,  Text = "second" },
            });
            var manifest = Manifest.Load(dir);
            var source = Translator.ReadSourceSegments(dir, manifest, "tr", out _);

            var translated = Translator.ApplyTranslations(source, new[] { "birinci", "ikinci" });
            Translator.WriteTranslatedVtt(dir, "tr", translated, usage: null);

            var cues = WebVtt.Read(File.ReadAllText(Path.Combine(dir, WebVtt.FileNameFor("tr"))));
            Assert.Equal(2, cues.Count);
            Assert.Equal(0.0, cues[0].StartSeconds);
            Assert.Equal(3.25, cues[0].EndSeconds);
            Assert.Equal(7.0, cues[1].EndSeconds);
            Assert.Equal("birinci", cues[0].Text);
            Assert.Equal("ikinci", cues[1].Text);
        }

        // ---- AC2: manifest map + RecordingLibrary reports the language --------

        [Fact]
        public void WriteTranslatedVtt_RegistersLanguage_LibraryReportsIt()
        {
            string leaf = "2026-07-10_141000_lang";
            string dir = MakeRecording(leaf);
            WriteSourceVtt(dir, OneCue("source text"));
            var manifest = Manifest.Load(dir);

            var translated = Translator.ApplyTranslations(
                Translator.ReadSourceSegments(dir, manifest, "tr", out _), new[] { "kaynak metin" });
            Translator.WriteTranslatedVtt(dir, "tr", translated, usage: null);

            var reloaded = Manifest.Load(dir);
            Assert.Equal(WebVtt.FileNameFor("tr"), reloaded.Transcripts["tr"]);
            Assert.True(File.Exists(Path.Combine(dir, WebVtt.FileNameFor("tr"))));

            var langs = RecordingLibrary.TranscriptLanguages(leaf, _root);
            Assert.NotNull(langs);
            Assert.Contains("en", langs!);   // source stays registered
            Assert.Contains("tr", langs!);   // new language added
        }

        // ---- AC3: re-translating an existing language overwrites --------------

        [Fact]
        public void WriteTranslatedVtt_ExistingLanguage_Overwrites_NoDuplicate()
        {
            string dir = MakeRecording("2026-07-10_142000_overwrite");
            WriteSourceVtt(dir, OneCue("hello"));
            var manifest = Manifest.Load(dir);
            var source = Translator.ReadSourceSegments(dir, manifest, "tr", out _);

            Translator.WriteTranslatedVtt(dir, "tr", Translator.ApplyTranslations(source, new[] { "merhaba" }), usage: null);
            Translator.WriteTranslatedVtt(dir, "tr", Translator.ApplyTranslations(source, new[] { "selam" }), usage: null);

            // One map entry, one Files entry, one file - the second run refreshed rather than duplicated.
            var reloaded = Manifest.Load(dir);
            Assert.Single(reloaded.Transcripts, kvp => kvp.Key == "tr");
            Assert.Single(reloaded.Files, f => f == WebVtt.FileNameFor("tr"));

            var cues = WebVtt.Read(File.ReadAllText(Path.Combine(dir, WebVtt.FileNameFor("tr"))));
            var only = Assert.Single(cues);
            Assert.Equal("selam", only.Text);   // latest translation won
        }

        // ---- AC4: guard / error paths -----------------------------------------

        [Fact]
        public void ReadSourceSegments_NoTranscript_Throws()
        {
            string dir = MakeRecording("2026-07-10_143000_notranscript");  // manifest only, no transcript
            var manifest = Manifest.Load(dir);
            var ex = Assert.Throws<UsageException>(() => Translator.ReadSourceSegments(dir, manifest, "tr", out _));
            Assert.Contains("no transcript to translate", ex.Message);
        }

        [Fact]
        public void ReadSourceSegments_FallsBackToTranscriptJson_WhenNoVtt()
        {
            string dir = MakeRecording("2026-07-10_143500_jsononly");
            // No per-language VTT, but a timed transcript.json exists (older recording shape).
            var segs = new List<TranscriptSegment>
            {
                new() { StartSeconds = 0.0, EndSeconds = 1.0, Text = "a" },
                new() { StartSeconds = 1.0, EndSeconds = 2.0, Text = "b" },
            };
            File.WriteAllText(Path.Combine(dir, "transcript.json"),
                System.Text.Json.JsonSerializer.Serialize(segs));
            var manifest = Manifest.Load(dir);   // Transcripts map is empty

            var read = Translator.ReadSourceSegments(dir, manifest, "tr", out _);
            Assert.Equal(2, read.Count);
            Assert.Equal("a", read[0].Text);
        }

        [Fact]
        public void ResolveDir_UnknownId_Throws()
        {
            var ex = Assert.Throws<UsageException>(() => Translator.ResolveDir("no-such-recording", _root));
            Assert.Contains("no recording found", ex.Message);
        }

        [Fact]
        public void ResolveDir_Traversal_Rejected()
        {
            Assert.Throws<UsageException>(() => Translator.ResolveDir("..\\escape", _root));
        }

        [Fact]
        public void NormalizeLanguage_Empty_Throws()
        {
            Assert.Throws<UsageException>(() => Translator.NormalizeLanguage("   "));
        }

        [Fact]
        public void NormalizeLanguage_Garbage_Throws()
        {
            var ex = Assert.Throws<UsageException>(() => Translator.NormalizeLanguage("tr; drop"));
            Assert.Contains("not a valid language code", ex.Message);
        }

        [Theory]
        [InlineData("tr", "tr")]
        [InlineData("ES", "es")]
        [InlineData("pt-BR", "pt-br")]
        public void NormalizeLanguage_WellFormed_LowercasesAndKeeps(string input, string expected)
        {
            Assert.Equal(expected, Translator.NormalizeLanguage(input));
        }

        // ---- AC5: request targets the shared model; parse + cost --------------

        [Fact]
        public void BuildRequestBody_TargetsSharedChatModel_AndCarriesCues()
        {
            string body = Translator.BuildRequestBody(new[] { "hello", "world" }, "tr", Translator.Model);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            Assert.Equal(Translator.Model, doc.RootElement.GetProperty("model").GetString());
            // The user message carries the cue array; the system message names the target language.
            var messages = doc.RootElement.GetProperty("messages");
            string user = messages[1].GetProperty("content").GetString()!;
            Assert.Contains("hello", user);
            Assert.Contains("world", user);
            string system = messages[0].GetProperty("content").GetString()!;
            Assert.Contains("Turkish", system);   // language-name hint for code 'tr'
        }

        [Fact]
        public void ParseResponse_ExtractsAlignedArray()
        {
            string body = ChatBody("[\"merhaba\", \"dunya\"]");
            var texts = Translator.ParseResponse(body, expectedCount: 2);
            Assert.Equal(new[] { "merhaba", "dunya" }, texts);
        }

        [Fact]
        public void ParseResponse_StripsCodeFence()
        {
            string body = ChatBody("```json\n[\"bir\", \"iki\"]\n```");
            var texts = Translator.ParseResponse(body, expectedCount: 2);
            Assert.Equal(new[] { "bir", "iki" }, texts);
        }

        [Fact]
        public void ParseResponse_WrongCount_Throws()
        {
            string body = ChatBody("[\"only-one\"]");
            var ex = Assert.Throws<UsageException>(() => Translator.ParseResponse(body, expectedCount: 3));
            Assert.Contains("cannot preserve cue timing", ex.Message);
        }

        [Fact]
        public void ParseResponse_NotAnArray_Throws()
        {
            string body = ChatBody("{\"text\": \"nope\"}");
            Assert.Throws<UsageException>(() => Translator.ParseResponse(body, expectedCount: 1));
        }

        // The accumulate itself moved to Ai.AiCostLedger, shared by every producer of AiCost (issue
        // #155). Its unit tests, and the tests that drive Translator.WriteTranslatedVtt against a
        // real manifest to prove the translator's cost survives the other writers, are in
        // AiCostLedgerTests.

        // ---- fixtures ----------------------------------------------------------

        /// <summary>A recording folder with only a manifest.json (Mode video), no transcript yet.</summary>
        private string MakeRecording(string leaf)
        {
            string dir = Path.Combine(_root, leaf);
            Directory.CreateDirectory(Path.Combine(dir, "shots"));
            ManifestStore.Replace(dir, new Manifest { Mode = "video", Label = leaf, CreatedUtc = DateTime.UtcNow.ToString("o") });
            return dir;
        }

        /// <summary>Write an English source VTT and register it in the manifest map (issue #98 shape).</summary>
        private static void WriteSourceVtt(string dir, List<TranscriptSegment> segments)
        {
            string name = WebVtt.FileNameFor(WebVtt.DefaultLanguage);
            File.WriteAllText(Path.Combine(dir, name), WebVtt.Write(segments));
            var m = Manifest.Load(dir);
            m.Transcripts[WebVtt.DefaultLanguage] = name;
            ManifestStore.Replace(dir, m);
        }

        private static List<TranscriptSegment> OneCue(string text) => new()
        {
            new() { StartSeconds = 0.0, EndSeconds = 2.0, Text = text },
        };

        /// <summary>Wrap assistant content in a minimal chat-completions response body.</summary>
        private static string ChatBody(string content) =>
            System.Text.Json.JsonSerializer.Serialize(new
            {
                choices = new object[] { new { message = new { role = "assistant", content } } },
            });
    }
}
