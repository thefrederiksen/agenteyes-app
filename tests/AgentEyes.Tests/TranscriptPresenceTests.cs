using System;
using System.IO;
using System.Windows;
using Xunit;
using AgentEyes;
using AgentEyes.App;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #4 - the desktop Library claimed a recording was transcribed when only a legacy flat
    /// transcript.txt existed. The Library card keyed its "Transcript" chip off transcript.txt
    /// EXISTING, and the detail window keyed its has-transcript flag off flat-text LENGTH, while
    /// the transcription backlog (and the Control API) treat the manifest-named transcript.json as
    /// completion - so the UI said "transcribed" about recordings that were in fact still queued.
    ///
    /// These tests pin every surface to the ONE canonical predicate (TranscriptStatus):
    ///
    /// * The predicate itself: transcript.json (manifest-named) = transcribed; transcript.txt
    ///   alone = flat text only, never "transcribed".
    /// * Criterion 1 - the Library card: a flat-text-only fixture must NOT show the Transcript
    ///   chip (it shows the quieter "Text file" chip instead). Swap the card back to
    ///   File.Exists(transcript.txt) and LibraryCard_FlatTextOnly_* fails.
    /// * Criterion 2 - the detail window: its decisions live in the extracted, testable
    ///   TranscriptPresentation (the window itself needs a WPF Application to construct, which is
    ///   exactly why the decision was pulled out of it - the issue's flagged assumption). Swap
    ///   HasTranscript back to text length and DetailPresentation_FlatTextOnly_* fails, because
    ///   the flat fixture HAS text but must NOT claim a transcript.
    /// * Criterion 5 - the flat text stays readable and copyable; the distinction never removes
    ///   access to content that already exists.
    /// * The Control API rows (RecordingLibrary): hasTranscript is canonical, the legacy file is
    ///   exposed separately as hasFlatTranscript - so the API and the desktop can never disagree
    ///   about the same recording again.
    /// </summary>
    public sealed class TranscriptPresenceTests : IDisposable
    {
        private readonly string _root;

        public TranscriptPresenceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "agenteyes-tx-presence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- fixtures ------------------------------------------------------

        /// <summary>A recording folder with a manifest, no transcript artifacts.</summary>
        private string Recording(string leaf)
        {
            string dir = Path.Combine(_root, leaf);
            Directory.CreateDirectory(dir);
            var m = new Manifest
            {
                Mode = "video",
                Label = leaf,
                CreatedUtc = "2026-08-01T10:00:00.0000000Z",
                DurationSeconds = 30,
            };
            File.WriteAllText(Path.Combine(dir, "recording.mp4"), "x");
            m.VideoFile = "recording.mp4";
            ManifestStore.Replace(dir, m);
            return dir;
        }

        /// <summary>The legacy shape: ONLY a flat transcript.txt, no transcript.json - the
        /// recording was never transcribed by the current pipeline.</summary>
        private string FlatTextOnlyRecording(string leaf, string text = "legacy flat words")
        {
            string dir = Recording(leaf);
            File.WriteAllText(Path.Combine(dir, "transcript.txt"), text);
            return dir;
        }

        /// <summary>The completed shape the pipeline writes: manifest-named transcript.json plus
        /// the flat transcript.txt alongside it.</summary>
        private string TranscribedRecording(string leaf)
        {
            string dir = Recording(leaf);
            File.WriteAllText(Path.Combine(dir, "transcript.json"),
                "[{\"StartSeconds\":0.0,\"EndSeconds\":1.5,\"Text\":\"hello\"}," +
                "{\"StartSeconds\":1.5,\"EndSeconds\":3.0,\"Text\":\"world\"}]");
            // The pipeline's human-readable rendering: one timestamped line per segment
            // (Package.WriteTranscript) - what the detail view has always displayed.
            File.WriteAllText(Path.Combine(dir, "transcript.txt"),
                "[00:00:00] hello" + Environment.NewLine + "[00:00:01] world");
            var m = Manifest.Load(dir);
            m.Transcript = "transcript.json";
            ManifestStore.Replace(dir, m);
            return dir;
        }

        // ---- the canonical predicate itself --------------------------------

        [Fact]
        public void Classify_FlatTextOnly_IsFlatTextOnlyNotTranscribed()
        {
            string dir = FlatTextOnlyRecording("flat");
            var m = Manifest.Load(dir);

            Assert.Equal(TranscriptKind.FlatTextOnly, TranscriptStatus.Classify(dir, m));
            Assert.False(TranscriptStatus.IsTranscribed(dir, m));
            Assert.True(TranscriptStatus.HasFlatText(dir));
        }

        [Fact]
        public void Classify_TranscriptJson_IsTranscribed()
        {
            string dir = TranscribedRecording("done");
            var m = Manifest.Load(dir);

            Assert.Equal(TranscriptKind.Transcribed, TranscriptStatus.Classify(dir, m));
            Assert.True(TranscriptStatus.IsTranscribed(dir, m));
        }

        [Fact]
        public void Classify_ManifestNamedArtifact_IsHonored()
        {
            // The manifest may name a non-default transcript artifact; the predicate must follow
            // the manifest, exactly as RecordingLibrary and the packager do.
            string dir = Recording("named");
            File.WriteAllText(Path.Combine(dir, "words.json"), "[]");
            var m = Manifest.Load(dir);
            m.Transcript = "words.json";
            ManifestStore.Replace(dir, m);

            Assert.Equal(TranscriptKind.Transcribed, TranscriptStatus.Classify(dir, Manifest.Load(dir)));
        }

        [Fact]
        public void Classify_NoArtifacts_IsNone()
        {
            string dir = Recording("bare");
            Assert.Equal(TranscriptKind.None, TranscriptStatus.Classify(dir, Manifest.Load(dir)));
        }

        [Fact]
        public void Classify_NullManifest_StillFindsDefaultNamesAndFlatText()
        {
            // An unreadable manifest must not hide artifacts that are plainly on disk.
            string dir = FlatTextOnlyRecording("nomanifest");
            Assert.Equal(TranscriptKind.FlatTextOnly, TranscriptStatus.Classify(dir, null));

            File.WriteAllText(Path.Combine(dir, "transcript.json"), "[]");
            Assert.Equal(TranscriptKind.Transcribed, TranscriptStatus.Classify(dir, null));
        }

        // ---- criterion 1: the Library card ---------------------------------

        [Fact]
        public void LibraryCard_FlatTextOnly_DoesNotShowTranscriptChip()
        {
            var card = RecentItem.From(FlatTextOnlyRecording("flat_card"));

            // The defect: this chip used to be driven by File.Exists(transcript.txt), which is
            // true for this fixture. The canonical predicate says NOT transcribed.
            Assert.NotEqual(Visibility.Visible, card.TranscriptChipVisibility);

            // The quieter affordance replaces it - the text is still one click away.
            Assert.Equal(Visibility.Visible, card.FlatTextChipVisibility);
        }

        [Fact]
        public void LibraryCard_TranscribedRecording_ShowsTranscriptChipOnly()
        {
            var card = RecentItem.From(TranscribedRecording("done_card"));

            Assert.Equal(Visibility.Visible, card.TranscriptChipVisibility);
            Assert.Equal(Visibility.Collapsed, card.FlatTextChipVisibility);
        }

        [Fact]
        public void LibraryCard_NoTranscript_ShowsNeitherChip()
        {
            var card = RecentItem.From(Recording("bare_card"));

            Assert.Equal(Visibility.Collapsed, card.TranscriptChipVisibility);
            Assert.Equal(Visibility.Collapsed, card.FlatTextChipVisibility);
        }

        [Fact]
        public void LibraryCard_CorruptManifest_StillClassifiesFromDisk()
        {
            // An unreadable manifest must not hide artifacts that are plainly on disk (review
            // finding): the chips are file facts, classified with a null manifest exactly like
            // the detail window's fallback.
            string dir = Path.Combine(_root, "corrupt_card");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), "{ not json");
            File.WriteAllText(Path.Combine(dir, "transcript.txt"), "still here");

            var card = RecentItem.From(dir);
            Assert.Equal(Visibility.Visible, card.FlatTextChipVisibility);
            Assert.Equal(Visibility.Collapsed, card.TranscriptChipVisibility);

            File.WriteAllText(Path.Combine(dir, "transcript.json"), "[]");
            card = RecentItem.From(dir);
            Assert.Equal(Visibility.Visible, card.TranscriptChipVisibility);
            Assert.Equal(Visibility.Collapsed, card.FlatTextChipVisibility);
        }

        // ---- criterion 2: the detail window's decision ---------------------

        [Fact]
        public void DetailPresentation_FlatTextOnly_MakesNoTranscriptClaim()
        {
            string dir = FlatTextOnlyRecording("flat_detail", "the old flat words");
            var p = TranscriptPresentation.For(dir, Manifest.Load(dir));

            // The defect: the window's flag used to be transcript-text LENGTH, which is > 0 here.
            Assert.False(p.HasTranscript);
            Assert.Equal(TranscriptKind.FlatTextOnly, p.Kind);
            Assert.NotNull(p.LegacyNotice);
        }

        [Fact]
        public void DetailPresentation_FlatTextOnly_TextStaysReadableAndCopyable()
        {
            // Criterion 5: the distinction must not remove access to content that already exists.
            string dir = FlatTextOnlyRecording("flat_access", "the old flat words");
            var p = TranscriptPresentation.For(dir, Manifest.Load(dir));

            Assert.Equal("the old flat words", p.Text);
            Assert.True(p.CanCopy);
        }

        [Fact]
        public void DetailPresentation_TranscribedRecording_ClaimsTranscriptAndKeepsFlatRendering()
        {
            string dir = TranscribedRecording("done_detail");
            var p = TranscriptPresentation.For(dir, Manifest.Load(dir));

            Assert.True(p.HasTranscript);
            Assert.Equal(TranscriptKind.Transcribed, p.Kind);
            // The displayed text is the pipeline's timestamped flat rendering, exactly what the
            // window showed before issue #4 - the presence CLAIM changed, the text did not
            // (review finding: joining the JSON segments would flatten the timecodes away).
            Assert.Equal("[00:00:00] hello" + Environment.NewLine + "[00:00:01] world", p.Text);
            Assert.True(p.CanCopy);
            Assert.Null(p.LegacyNotice);
        }

        [Fact]
        public void DetailPresentation_TranscribedWithoutFlatFile_FallsBackToJsonText()
        {
            // A transcribed recording whose flat rendering was deleted still shows its text,
            // read from the JSON segments through the same reader the Control API serves.
            string dir = TranscribedRecording("done_nofla");
            File.Delete(Path.Combine(dir, "transcript.txt"));
            var p = TranscriptPresentation.For(dir, Manifest.Load(dir));

            Assert.True(p.HasTranscript);
            Assert.Equal("hello world", p.Text);
            Assert.True(p.CanCopy);
        }

        [Fact]
        public void DetailPresentation_EmptyFlatTextOnly_NoNoticeNoCopy()
        {
            // A 0-byte legacy file: the chip-level classification is FlatTextOnly (the file
            // exists), but nothing is shown, so no "showing the text file" caption may stack
            // over the empty-state placeholder and there is nothing to copy.
            string dir = FlatTextOnlyRecording("flat_empty", "");
            var p = TranscriptPresentation.For(dir, Manifest.Load(dir));

            Assert.False(p.HasTranscript);
            Assert.Equal(TranscriptKind.FlatTextOnly, p.Kind);
            Assert.Equal("", p.Text);
            Assert.False(p.CanCopy);
            Assert.Null(p.LegacyNotice);
        }

        [Fact]
        public void DetailPresentation_NoTranscript_NoClaimNoCopy()
        {
            var p = TranscriptPresentation.For(Recording("bare_detail"), null);

            Assert.False(p.HasTranscript);
            Assert.Equal(TranscriptKind.None, p.Kind);
            Assert.Equal("", p.Text);
            Assert.False(p.CanCopy);
        }

        // ---- the Control API rows say the same thing -----------------------

        [Fact]
        public void Library_FlatTextOnly_HasTranscriptFalse_HasFlatTranscriptTrue()
        {
            FlatTextOnlyRecording("flat_api");

            var page = RecordingLibrary.List(limit: 10, offset: 0, root: _root);
            var row = Assert.Single(page.Items);
            Assert.False(row.HasTranscript);
            Assert.True(row.HasFlatTranscript);

            var detail = RecordingLibrary.GetDetail("flat_api", _root);
            Assert.NotNull(detail);
            Assert.False(detail!.HasTranscript);
            Assert.True(detail.HasFlatTranscript);
        }

        [Fact]
        public void Library_TranscribedRecording_HasTranscriptTrue()
        {
            TranscribedRecording("done_api");

            var detail = RecordingLibrary.GetDetail("done_api", _root);
            Assert.NotNull(detail);
            Assert.True(detail!.HasTranscript);
            Assert.True(detail.HasFlatTranscript);   // the pipeline writes both; both are reported
        }
    }
}
