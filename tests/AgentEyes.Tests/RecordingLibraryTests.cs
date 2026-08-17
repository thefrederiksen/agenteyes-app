using System;
using System.IO;
using Xunit;
using AgentEyes;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Read/browse helpers behind the Control API S1 read surface (issue #73): the recordings
    /// list (paging + newest-first + media/transcript flags), one recording's detail, its marker
    /// shots, and its transcript - including the not_found paths (unknown id, no transcript) that
    /// the API maps to HTTP 404 code:"not_found". All fixtures live under a temp root so nothing
    /// touches the real recordings folder.
    /// </summary>
    public class RecordingLibraryTests : IDisposable
    {
        private readonly string _root;

        public RecordingLibraryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-lib-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- fixtures -----------------------------------------------------

        private string MakeRecording(string leaf, string mode = "video",
            bool withVideo = false, bool withAudio = false, Action<Manifest>? tweak = null)
        {
            string dir = Path.Combine(_root, leaf);
            Directory.CreateDirectory(Path.Combine(dir, "shots"));
            var m = new Manifest
            {
                Mode = mode,
                Label = leaf,
                CreatedUtc = "2026-06-10T00:00:00.0000000Z",
                DurationSeconds = 12.5,
            };
            if (withVideo) { File.WriteAllText(Path.Combine(dir, "recording.mp4"), "x"); m.VideoFile = "recording.mp4"; }
            if (withAudio) { File.WriteAllText(Path.Combine(dir, "audio.wav"), "x"); m.AudioFile = "audio.wav"; }
            tweak?.Invoke(m);
            ManifestStore.Replace(dir, m);
            return dir;
        }

        private void AddShot(string dir, string relFile, double offset)
        {
            string abs = Path.Combine(dir, relFile);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, "png");
            var m = Manifest.Load(dir);
            m.Shots.Add(new Manifest.ShotEntry { OffsetSeconds = offset, File = relFile });
            ManifestStore.Replace(dir, m);
        }

        private void WriteTranscriptJson(string dir)
        {
            File.WriteAllText(Path.Combine(dir, "transcript.json"),
                "[{\"StartSeconds\":0.0,\"EndSeconds\":1.5,\"Text\":\"hello\"}," +
                "{\"StartSeconds\":1.5,\"EndSeconds\":3.0,\"Text\":\"world\"}]");
            var m = Manifest.Load(dir);
            m.Transcript = "transcript.json";
            ManifestStore.Replace(dir, m);
        }

        // ---- List ---------------------------------------------------------

        [Fact]
        public void List_MultipleRecordings_NewestFirstWithTotalAndPaging()
        {
            MakeRecording("2026-06-10_100000_a");
            MakeRecording("2026-06-10_100001_b");
            MakeRecording("2026-06-10_100002_c");

            var page1 = RecordingLibrary.List(limit: 2, offset: 0, root: _root);
            Assert.Equal(3, page1.Total);
            Assert.Equal(2, page1.Items.Count);
            Assert.Equal("2026-06-10_100002_c", page1.Items[0].Id);   // newest first
            Assert.Equal("2026-06-10_100001_b", page1.Items[1].Id);

            var page2 = RecordingLibrary.List(limit: 2, offset: 2, root: _root);
            Assert.Equal(3, page2.Total);
            Assert.Single(page2.Items);
            Assert.Equal("2026-06-10_100000_a", page2.Items[0].Id);
        }

        [Fact]
        public void List_Item_CarriesMediaAndTranscriptFlags()
        {
            string dir = MakeRecording("2026-06-10_120000_full", withVideo: true, withAudio: true);
            AddShot(dir, "shots/frame_001.png", 0);
            WriteTranscriptJson(dir);

            var page = RecordingLibrary.List(limit: 50, offset: 0, root: _root);
            var item = Assert.Single(page.Items);
            Assert.True(item.HasVideo);
            Assert.True(item.HasAudio);
            Assert.True(item.HasTranscript);
            Assert.Equal(1, item.ShotCount);
            Assert.Equal("video", item.Mode);
            Assert.True(Path.IsPathRooted(item.Dir));
        }

        [Fact]
        public void List_EmptyRoot_ReturnsZeroTotalNoItems()
        {
            var page = RecordingLibrary.List(limit: 50, offset: 0, root: _root);
            Assert.Equal(0, page.Total);
            Assert.Empty(page.Items);
        }

        // ---- GetDetail ----------------------------------------------------

        [Fact]
        public void GetDetail_ExistingId_ReturnsManifestAndResolvedBooleans()
        {
            MakeRecording("2026-06-10_130000_rec", withVideo: true);

            var d = RecordingLibrary.GetDetail("2026-06-10_130000_rec", _root);
            Assert.NotNull(d);
            Assert.Equal("2026-06-10_130000_rec", d!.Id);
            Assert.True(Path.IsPathRooted(d.Dir));
            Assert.True(d.HasVideo);
            Assert.False(d.HasAudio);
            Assert.Equal("video", d.Manifest.Mode);
        }

        [Fact]
        public void GetDetail_UnknownId_ReturnsNull()
        {
            Assert.Null(RecordingLibrary.GetDetail("does-not-exist", _root));
        }

        [Theory]
        [InlineData("..")]
        [InlineData("a/b")]
        [InlineData("a\\b")]
        public void GetDetail_SeparatorOrTraversalId_ReturnsNull(string id)
        {
            Assert.Null(RecordingLibrary.GetDetail(id, _root));
        }

        // ---- GetShots -----------------------------------------------------

        [Fact]
        public void GetShots_RecordingWithShots_ReturnsAbsoluteExistingPaths()
        {
            string dir = MakeRecording("2026-06-10_140000_shots", withVideo: true);
            AddShot(dir, "shots/frame_001.png", 5.0);

            var shots = RecordingLibrary.GetShots("2026-06-10_140000_shots", _root);
            Assert.NotNull(shots);
            var shot = Assert.Single(shots!);
            Assert.Equal("shots/frame_001.png", shot.File);
            Assert.Equal(5.0, shot.OffsetSeconds);
            Assert.True(Path.IsPathRooted(shot.Path));
            Assert.True(File.Exists(shot.Path));
        }

        [Fact]
        public void GetShots_RecordingWithoutShots_ReturnsEmptyNotNull()
        {
            MakeRecording("2026-06-10_141000_bare");
            var shots = RecordingLibrary.GetShots("2026-06-10_141000_bare", _root);
            Assert.NotNull(shots);
            Assert.Empty(shots!);
        }

        [Fact]
        public void GetShots_UnknownId_ReturnsNull()
        {
            Assert.Null(RecordingLibrary.GetShots("does-not-exist", _root));
        }

        // ---- GetTranscript ------------------------------------------------

        [Fact]
        public void GetTranscript_WithJson_ReturnsTextAndSegments()
        {
            string dir = MakeRecording("2026-06-10_150000_tx", withVideo: true);
            WriteTranscriptJson(dir);

            var t = RecordingLibrary.GetTranscript("2026-06-10_150000_tx", _root);
            Assert.NotNull(t);
            Assert.Equal("hello world", t!.Text);
            Assert.Equal(2, t.Segments.Count);
            Assert.Equal(0.0, t.Segments[0].Start);
            Assert.Equal(1.5, t.Segments[0].End);
            Assert.Equal("hello", t.Segments[0].Text);
        }

        [Fact]
        public void GetTranscript_FlatTextOnly_ReturnsTextWithEmptySegments()
        {
            string dir = MakeRecording("2026-06-10_151000_txt", withVideo: true);
            File.WriteAllText(Path.Combine(dir, "transcript.txt"), "just some text");

            var t = RecordingLibrary.GetTranscript("2026-06-10_151000_txt", _root);
            Assert.NotNull(t);
            Assert.Equal("just some text", t!.Text);
            Assert.Empty(t.Segments);
        }

        [Fact]
        public void GetTranscript_RecordingExistsButNoTranscript_ReturnsNull()
        {
            MakeRecording("2026-06-10_152000_none", withVideo: true);
            Assert.Null(RecordingLibrary.GetTranscript("2026-06-10_152000_none", _root));
        }

        [Fact]
        public void GetTranscript_UnknownId_ReturnsNull()
        {
            Assert.Null(RecordingLibrary.GetTranscript("does-not-exist", _root));
        }

        // ---- TranscriptLanguages (issue #98) ------------------------------

        [Fact]
        public void TranscriptLanguages_FolderWithTwoVttFiles_ReturnsBothLanguagesSorted()
        {
            string dir = MakeRecording("2026-06-10_160000_vtt", withVideo: true, tweak: m =>
            {
                m.Transcript = "transcript.json";
                m.Transcripts["es"] = "transcript.es.vtt";
                m.Transcripts["en"] = "transcript.en.vtt";
            });
            File.WriteAllText(Path.Combine(dir, "transcript.en.vtt"), "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nhi\n\n");
            File.WriteAllText(Path.Combine(dir, "transcript.es.vtt"), "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nhola\n\n");

            var langs = RecordingLibrary.TranscriptLanguages("2026-06-10_160000_vtt", _root);
            Assert.NotNull(langs);
            Assert.Equal(new[] { "en", "es" }, langs!);
        }

        [Fact]
        public void TranscriptLanguages_OldRecordingWithoutMap_ReturnsEmptyNotNull()
        {
            MakeRecording("2026-06-10_161000_old", withVideo: true);   // no Transcripts map
            var langs = RecordingLibrary.TranscriptLanguages("2026-06-10_161000_old", _root);
            Assert.NotNull(langs);
            Assert.Empty(langs!);
        }

        [Fact]
        public void TranscriptLanguages_UnknownId_ReturnsNull()
        {
            Assert.Null(RecordingLibrary.TranscriptLanguages("does-not-exist", _root));
        }

        [Fact]
        public void List_OldRecordingWithJsonButNoMap_StillReportsHasTranscript()
        {
            // Issue #98 backward compatibility: a recording predating the per-language map still has
            // transcript.json, so it must still be flagged as having a transcript.
            string dir = MakeRecording("2026-06-10_162000_legacy", withVideo: true);
            WriteTranscriptJson(dir);   // sets Transcript = transcript.json, leaves Transcripts empty

            var page = RecordingLibrary.List(limit: 50, offset: 0, root: _root);
            var item = Assert.Single(page.Items);
            Assert.True(item.HasTranscript);
        }
    }
}
