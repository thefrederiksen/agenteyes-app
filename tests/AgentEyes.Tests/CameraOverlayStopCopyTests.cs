using System;
using System.Collections.Generic;
using System.Linq;
using AgentEyes;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #36, AC4 - the ONE step where overlay geometry gets silently dropped.
    ///
    /// The stop does not write the manifest whole. Since issue #155 it is a READ-MODIFY-WRITE:
    /// <c>RecordingService.SaveManifest</c> fills a session-local manifest and then copies the fields
    /// this session owns into the record already on disk. A field set on the session object but
    /// forgotten in that copy is written NOWHERE, and every other test still passes - the model is
    /// fine, the serializer is fine, the store is fine. That is exactly the shape of defect this
    /// repo has paid for before.
    ///
    /// So this does not enumerate the four fields by hand. It asks the MANIFEST which overlay fields
    /// exist (reflection), and requires each one to appear on BOTH SIDES of the copy. Adding a fifth
    /// overlay field to Manifest.cs therefore fails this test until the copy is updated.
    ///
    /// WHAT IT CANNOT SEE, stated rather than implied: it reads the SOURCE of one method, so a copy
    /// performed by some other helper, by reflection, or under a different spelling would not be
    /// recognised - it would report a missing copy that in fact exists. That direction is safe (it
    /// fails, and a human looks); the reverse is what matters and is closed, because a field that is
    /// copied nowhere cannot appear in this method's text. It also cannot prove the copied value is
    /// the one the person chose - that is the running-app manifest in the proof.
    /// </summary>
    public class CameraOverlayStopCopyTests
    {
        private const string ServicePath = @"src\AgentEyes.Core\RecordingService.cs";

        /// <summary>Every manifest property that carries overlay framing, asked of the type itself.</summary>
        private static List<string> OverlayFields() =>
            typeof(Manifest).GetProperties()
                .Where(p => p.Name.StartsWith("PreviewOverlay", StringComparison.Ordinal))
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

        [Fact]
        public void Manifest_CarriesTheFourOverlayFieldsThisIssueAddsTo()
        {
            // The instrument first: an empty list would make every assertion below vacuous.
            var fields = OverlayFields();

            Assert.Equal(
                new[] { "PreviewOverlayCircle", "PreviewOverlayCorner", "PreviewOverlayInset", "PreviewOverlayShape" },
                fields);
        }

        [Fact]
        public void EveryOverlayFieldTheSessionSets_IsAlsoCopiedIntoTheManifestOnDisk()
        {
            var fields = OverlayFields();
            Assert.NotEmpty(fields);

            string save = RepoSource.MethodBody(RepoSource.Read(ServicePath), "void SaveManifest()");
            Assert.Contains("ManifestStore.Update(", save);

            var notSet = fields.Where(f => !save.Contains($"manifest.{f} =", StringComparison.Ordinal)).ToList();
            Assert.True(notSet.Count == 0,
                "The stop never fills these overlay fields on the session manifest: " + string.Join(", ", notSet));

            var notCopied = fields.Where(f => !save.Contains($"m.{f} = manifest.{f};", StringComparison.Ordinal)).ToList();
            Assert.True(notCopied.Count == 0,
                "These overlay fields are set at the stop but never copied into the manifest on disk, "
                + "so they reach no file at all: " + string.Join(", ", notCopied));
        }

        [Fact]
        public void TheSessionsFraming_IsTakenUnderTheLockWithTheRestOfTheSessionState()
        {
            // The framing written to the manifest must be THIS recording's, not one a recording
            // started a moment later chose. Issue #33 took the corner under the state lock for
            // exactly this reason; issue #36 widens it from a string to the whole framing.
            string stop = RepoSource.Read(ServicePath);
            Assert.Contains("previewOverlay = _previewOverlay;", stop);
            Assert.Contains("CameraOverlaySettings? previewOverlay;", stop);
        }

        [Fact]
        public void TheRecordedFraming_IsCopiedAndCanonicalised_NotTheCallersObject()
        {
            // A HUD click after the stop must not be able to rewrite what the recording says it was
            // framed with, and a spelling nothing produces must not reach the file.
            string body = RepoSource.MethodBody(RepoSource.Read(ServicePath),
                                                "public void SetPreviewOverlay(CameraOverlaySettings? overlay)");

            Assert.Contains("overlay?.Canonical()", body);
            Assert.Contains("_previewOverlay = copy;", body);
        }
    }
}
