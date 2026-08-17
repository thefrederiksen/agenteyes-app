using System;
using System.Collections.Generic;
using System.IO;
using AgentEyes;
using AgentEyes.Packaging;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The subtitle burn-in engine (issue #102). Covers the pieces that do NOT launch ffmpeg:
    /// (AC1) the output file name for a language is recording.&lt;lang&gt;.subtitled.mp4;
    /// (AC2) is asserted in <see cref="FfmpegArgsTests"/> (the exact ffmpeg args string);
    /// (AC3) registering the output adds it to the manifest Files and RecordingLibrary reports it,
    ///       idempotently (re-burn does not duplicate) and the source video is never overwritten;
    /// (AC4) requesting a language whose transcript.&lt;lang&gt;.vtt does not exist throws a clear error
    ///       (before ffmpeg runs, so no zero-byte output), as does an audio-only recording / unknown id.
    /// </summary>
    public class SubtitleBurnerTests : IDisposable
    {
        private readonly string _root;

        public SubtitleBurnerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-subtitle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- AC1: output naming -----------------------------------------------

        [Theory]
        [InlineData("tr", "recording.tr.subtitled.mp4")]
        [InlineData("es", "recording.es.subtitled.mp4")]
        [InlineData("pt-br", "recording.pt-br.subtitled.mp4")]
        public void OutputFileName_IsRecordingLangSubtitledMp4(string lang, string expected)
        {
            Assert.Equal(expected, SubtitleBurner.OutputFileName(lang));
        }

        // ---- AC3: manifest registration + RecordingLibrary --------------------

        [Fact]
        public void RegisterOutput_AddsToManifestFiles_LibraryDetailReportsIt()
        {
            string leaf = "2026-07-10_150000_burned";
            string dir = MakeRecording(leaf);
            var manifest = Manifest.Load(dir);

            SubtitleBurner.RegisterOutput(dir, SubtitleBurner.OutputFileName("tr"));

            var reloaded = Manifest.Load(dir);
            Assert.Contains("recording.tr.subtitled.mp4", reloaded.Files);

            var detail = RecordingLibrary.GetDetail(leaf, _root);
            Assert.NotNull(detail);
            Assert.Contains("recording.tr.subtitled.mp4", detail!.Manifest.Files);
        }

        [Fact]
        public void RegisterOutput_ReBurn_DoesNotDuplicate()
        {
            string dir = MakeRecording("2026-07-10_150100_rebrun");
            var manifest = Manifest.Load(dir);
            string outputName = SubtitleBurner.OutputFileName("tr");

            SubtitleBurner.RegisterOutput(dir, outputName);
            SubtitleBurner.RegisterOutput(dir, outputName);

            var reloaded = Manifest.Load(dir);
            Assert.Single(reloaded.Files, f => f == outputName);
        }

        // ---- AC3: source video resolution / never-overwrite -------------------

        [Fact]
        public void ResolveVideoFile_ReturnsSource_AndOutputNameDiffers()
        {
            string dir = MakeRecording("2026-07-10_150200_source");
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), "fake mp4 bytes");
            var manifest = Manifest.Load(dir);

            string src = SubtitleBurner.ResolveVideoFile(dir, manifest);
            Assert.Equal(Path.Combine(dir, "recording.mp4"), src);
            // The derived output has a distinct name, so the source is never overwritten.
            Assert.NotEqual("recording.mp4", SubtitleBurner.OutputFileName("tr"));
        }

        [Fact]
        public void ResolveVideoFile_NoVideo_Throws()
        {
            string dir = MakeRecording("2026-07-10_150300_novideo");   // manifest only, no recording.mp4
            var manifest = Manifest.Load(dir);
            var ex = Assert.Throws<UsageException>(() => SubtitleBurner.ResolveVideoFile(dir, manifest));
            Assert.Contains("no video", ex.Message);
        }

        // ---- AC4: missing-VTT guard (no partial output) -----------------------

        [Fact]
        public void ResolveSubtitlePath_MissingLanguage_Throws_AndLeavesNoOutput()
        {
            string dir = MakeRecording("2026-07-10_150400_missingvtt");
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), "fake mp4 bytes");
            var manifest = Manifest.Load(dir);   // no transcript of any kind

            var ex = Assert.Throws<UsageException>(() => SubtitleBurner.ResolveSubtitlePath(dir, manifest, "tr"));
            Assert.Contains("transcript.tr.vtt", ex.Message);

            // The guard runs before ffmpeg, so no subtitled output file exists.
            Assert.False(File.Exists(Path.Combine(dir, SubtitleBurner.OutputFileName("tr"))));
        }

        [Fact]
        public void ResolveSubtitlePath_ExistingLanguage_ReturnsPath()
        {
            string dir = MakeRecording("2026-07-10_150500_hasvtt");
            WriteSourceVtt(dir, "tr", "merhaba");
            var manifest = Manifest.Load(dir);

            string path = SubtitleBurner.ResolveSubtitlePath(dir, manifest, "tr");
            Assert.Equal(Path.Combine(dir, WebVtt.FileNameFor("tr")), path);
        }

        [Fact]
        public void ResolveDir_UnknownId_Throws()
        {
            var ex = Assert.Throws<UsageException>(() => SubtitleBurner.ResolveDir("no-such-recording", _root));
            Assert.Contains("no recording found", ex.Message);
        }

        [Fact]
        public void ResolveDir_Traversal_Rejected()
        {
            Assert.Throws<UsageException>(() => SubtitleBurner.ResolveDir("..\\escape", _root));
        }

        // ---- fixtures ----------------------------------------------------------

        /// <summary>A recording folder with only a manifest.json (Mode video).</summary>
        private string MakeRecording(string leaf)
        {
            string dir = Path.Combine(_root, leaf);
            Directory.CreateDirectory(Path.Combine(dir, "shots"));
            ManifestStore.Replace(dir, new Manifest { Mode = "video", Label = leaf, VideoFile = "recording.mp4", CreatedUtc = DateTime.UtcNow.ToString("o") });
            return dir;
        }

        /// <summary>Write a per-language source VTT and register it in the manifest map (issue #98 shape).</summary>
        private static void WriteSourceVtt(string dir, string lang, string text)
        {
            string name = WebVtt.FileNameFor(lang);
            var segs = new List<TranscriptSegment> { new() { StartSeconds = 0.0, EndSeconds = 2.0, Text = text } };
            File.WriteAllText(Path.Combine(dir, name), WebVtt.Write(segs));
            var m = Manifest.Load(dir);
            m.Transcripts[lang] = name;
            ManifestStore.Replace(dir, m);
        }
    }
}
