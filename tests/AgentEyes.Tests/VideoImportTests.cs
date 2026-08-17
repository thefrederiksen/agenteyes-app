using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentEyes;
using AgentEyes.Packaging;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// The external-video import engine (issue #100). Covers the pure, network-free pieces:
    /// (1) the source guard rejects a missing file / non-video extension with a clear error (AC4);
    /// (2) the manifest is marked imported and records the source file name, Mode stays "video" (AC3);
    /// (3) the artifact writer produces transcript.json/txt and a multi-cue (or one-cue fallback) VTT (AC1/AC5);
    /// (4) a folder produced by the engine's pieces is a normal library entry - HasVideo + HasTranscript (AC2).
    /// Transcription is "mocked" by supplying the transcript segments directly, so no ffmpeg or network runs.
    /// </summary>
    public class VideoImportTests : IDisposable
    {
        private readonly string _root;

        public VideoImportTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- AC4: source guard -------------------------------------------------

        [Fact]
        public void ValidateSource_MissingFile_Throws()
        {
            string missing = Path.Combine(_root, "does-not-exist.mp4");
            var ex = Assert.Throws<UsageException>(() => VideoImport.ValidateSource(missing));
            Assert.Contains("not found", ex.Message);
        }

        [Fact]
        public void ValidateSource_NonVideoExtension_Throws()
        {
            string txt = Path.Combine(_root, "notes.txt");
            File.WriteAllText(txt, "hello");
            var ex = Assert.Throws<UsageException>(() => VideoImport.ValidateSource(txt));
            Assert.Contains("not a supported video file", ex.Message);
        }

        [Fact]
        public void ValidateSource_EmptyPath_Throws()
        {
            Assert.Throws<UsageException>(() => VideoImport.ValidateSource(""));
        }

        [Theory]
        [InlineData("meeting.mp4")]
        [InlineData("call.MKV")]     // extension match is case-insensitive
        [InlineData("clip.webm")]
        public void ValidateSource_ExistingVideoFile_DoesNotThrow(string name)
        {
            string file = Path.Combine(_root, name);
            File.WriteAllText(file, "x");     // presence + extension is all the pure guard checks
            VideoImport.ValidateSource(file); // must not throw
        }

        // ---- AC3: manifest marks the entry imported ----------------------------

        [Fact]
        public void BuildManifest_MarksImported_WithSourceNameAndVideoMode()
        {
            var created = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
            var m = VideoImport.BuildManifest("TeamsMeeting.mp4", durationSeconds: 92.3456, createdUtc: created);

            Assert.True(m.Imported);
            Assert.Equal("TeamsMeeting.mp4", m.ImportedSource);
            Assert.Equal("video", m.Mode);                 // it IS a video recording
            Assert.Equal("TeamsMeeting.mp4", m.VideoFile);  // copied in preserving its name
            Assert.Equal("TeamsMeeting", m.Label);
            // 92.3456 is a NON-midpoint input (third decimal onward is "456", well above .005), so it
            // rounds to 92.35 regardless of MidpointRounding mode or IEEE-754 representation - no longer
            // fragile like the old midpoint value 92.345 (issue #114).
            Assert.Equal(92.35, m.DurationSeconds);         // rounded to 2 dp
            Assert.Equal(created.ToString("o"), m.CreatedUtc);
        }

        [Fact]
        public void Manifest_RoundTrips_ImportedFlagAndSource()
        {
            string dir = Path.Combine(_root, "rt");
            Directory.CreateDirectory(dir);
            ManifestStore.Replace(dir, VideoImport.BuildManifest("x.mp4", 1.0, DateTime.UtcNow));

            var loaded = Manifest.Load(dir);
            Assert.True(loaded.Imported);
            Assert.Equal("x.mp4", loaded.ImportedSource);
        }

        [Fact]
        public void Manifest_OldWithoutImportedField_DefaultsToNativeNotImported()
        {
            // Backward compatibility: a manifest.json predating issue #100 has no "Imported" property.
            string dir = Path.Combine(_root, "legacy");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                "{\"Mode\":\"video\",\"Label\":\"old\",\"VideoFile\":\"recording.mp4\"}");

            var loaded = Manifest.Load(dir);
            Assert.False(loaded.Imported);
            Assert.Null(loaded.ImportedSource);
        }

        // ---- AC1/AC5: artifact writer -----------------------------------------

        [Fact]
        public void WriteArtifacts_MultiSegment_WritesJsonTxtAndMultiCueVtt()
        {
            string dir = MakeFolderWithVideo("2026-07-10_120000_multi", "multi.mp4");
            var m = VideoImport.BuildManifest("multi.mp4", 9.25, DateTime.UtcNow);
            var segments = new List<TranscriptSegment>
            {
                new() { StartSeconds = 0.0, EndSeconds = 2.0,  Text = "first cue" },
                new() { StartSeconds = 2.0, EndSeconds = 5.5,  Text = "second cue" },
                new() { StartSeconds = 5.5, EndSeconds = 9.25, Text = "third cue" },
            };

            // Issue #155: WriteArtifacts now finalizes the manifest ON DISK (a read-modify-write),
            // exactly as the import does - so the record has to be there first, as it is in production.
            ManifestStore.Replace(dir, m);

            var saved = VideoImport.WriteArtifacts(dir, segments, named: null);

            Assert.True(File.Exists(Path.Combine(dir, "transcript.json")));
            Assert.True(File.Exists(Path.Combine(dir, "transcript.txt")));

            string vttName = WebVtt.FileNameFor(WebVtt.DefaultLanguage);   // transcript.en.vtt
            string vttPath = Path.Combine(dir, vttName);
            Assert.True(File.Exists(vttPath));

            var cues = WebVtt.Read(File.ReadAllText(vttPath));
            Assert.Equal(3, cues.Count);                                   // multiple cues (AC5)
            Assert.Equal("first cue", cues[0].Text);

            // The manifest now points at the transcript artifacts (issue #98 per-language map).
            Assert.Equal("transcript.json", saved.Transcript);
            Assert.Equal(vttName, saved.Transcripts[WebVtt.DefaultLanguage]);
        }

        [Fact]
        public void WriteArtifacts_SingleSegment_WritesValidOneCueVtt()
        {
            string dir = MakeFolderWithVideo("2026-07-10_121000_single", "single.mp4");
            var m = VideoImport.BuildManifest("single.mp4", 12.5, DateTime.UtcNow);
            var segments = new List<TranscriptSegment>
            {
                new() { StartSeconds = 0.0, EndSeconds = 12.5, Text = "the whole clip as one block" },
            };

            ManifestStore.Replace(dir, m);
            VideoImport.WriteArtifacts(dir, segments, named: null);

            string vttPath = Path.Combine(dir, WebVtt.FileNameFor(WebVtt.DefaultLanguage));
            var cues = WebVtt.Read(File.ReadAllText(vttPath));
            var only = Assert.Single(cues);                                // one valid cue (fallback)
            Assert.Equal("the whole clip as one block", only.Text);
            Assert.Equal(0.0, only.StartSeconds);
            Assert.Equal(12.5, only.EndSeconds);
        }

        // ---- AC2: an imported folder is a normal library entry -----------------

        [Fact]
        public void ImportedFolder_ListsAsNormalRecording_WithVideoAndTranscript()
        {
            // Build a recording folder exactly the way the engine's pieces do (transcription mocked
            // by supplying segments), then browse it through the real RecordingLibrary.
            string leaf = "2026-07-10_130000_meeting";
            string dir = MakeFolderWithVideo(leaf, "meeting.mp4");
            var m = VideoImport.BuildManifest("meeting.mp4", 30.0, DateTime.UtcNow);
            ManifestStore.Replace(dir, m);
            VideoImport.WriteArtifacts(dir, new List<TranscriptSegment>
            {
                new() { StartSeconds = 0.0, EndSeconds = 3.0, Text = "hello world" },
            }, named: null);

            var page = RecordingLibrary.List(limit: 50, offset: 0, root: _root);
            var item = Assert.Single(page.Items);
            Assert.Equal(leaf, item.Id);
            Assert.True(item.HasVideo);
            Assert.True(item.HasTranscript);
            Assert.Equal("video", item.Mode);

            var detail = RecordingLibrary.GetDetail(leaf, _root);
            Assert.NotNull(detail);
            Assert.True(detail!.HasVideo);
            Assert.True(detail.HasTranscript);
            Assert.True(detail.Manifest.Imported);
            Assert.Equal("meeting.mp4", detail.Manifest.ImportedSource);

            var langs = RecordingLibrary.TranscriptLanguages(leaf, _root);
            Assert.Equal(new[] { WebVtt.DefaultLanguage }, langs);
        }

        // ---- fixtures ----------------------------------------------------------

        /// <summary>Create a session folder under the temp root holding a stand-in video file (the
        /// import engine copies the real video here; for these pure tests a placeholder is enough).</summary>
        private string MakeFolderWithVideo(string leaf, string videoName)
        {
            string dir = Path.Combine(_root, leaf);
            Directory.CreateDirectory(Path.Combine(dir, "shots"));
            File.WriteAllText(Path.Combine(dir, videoName), "video-bytes");
            return dir;
        }
    }
}
