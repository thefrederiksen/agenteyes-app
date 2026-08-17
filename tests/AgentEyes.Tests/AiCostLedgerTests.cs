using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AgentEyes;
using AgentEyes.Ai;
using AgentEyes.Packaging;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #155: <see cref="Manifest.AiCost"/> is the recording's TOTAL AI spend, so every writer
    /// of it must add to what is already there.
    ///
    /// Routing every writer through one atomic, serialized path fixed torn files and stale-copy
    /// overwrites - it did not fix this, because here both writes are legitimate and both land. The
    /// translator accumulated; packaging, the title backfill and the video import each assigned a
    /// brand-new <see cref="AiCostInfo"/> over the top. No concurrency was needed to lose real
    /// accounting: title generation fails (non-fatal by design), the user translates the transcript,
    /// the repair pass later titles the recording, and the translation's tokens are gone.
    ///
    /// These drive the REAL writers against a real manifest - no network, because the generation is
    /// the only part that needs one and the write is the part that was wrong.
    /// </summary>
    public sealed class AiCostLedgerTests : IDisposable
    {
        private readonly string _dir;

        public AiCostLedgerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "AgentEyes-cost-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_dir, "shots"));
            ManifestStore.Replace(_dir, new Manifest
            {
                Mode = "video",
                Label = "video",
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                VideoFile = "recording.mp4",
            });
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private static readonly IReadOnlyList<TranscriptSegment> Cues = new[]
        {
            new TranscriptSegment { StartSeconds = 0, EndSeconds = 1.5, Text = "hello" },
        };

        private static TitleGenerator.TitleResult Named(int prompt, int completion, string model = "title-model") =>
            new("A title", "One line.", new AiUsage(prompt, completion), model);

        private Manifest Loaded() => Manifest.Load(_dir);

        // ---- the sequence the review named, in order ----

        [Fact]
        public void FailedTitle_ThenTranslation_ThenTitleBackfill_KeepsTheTranslationCost()
        {
            // 1. Packaging runs; title generation FAILED, so nothing names the recording and no cost
            //    is recorded (named = null).
            Package.FinalizeManifest(_dir, Array.Empty<WalkthroughShot>(), named: null);
            Assert.Null(Loaded().AiCost);

            // 2. The user translates the transcript. That spend is real and is recorded.
            Translator.WriteTranslatedVtt(_dir, "es", Cues, new AiUsage(400, 90));
            Assert.Equal(400, Loaded().AiCost!.PromptTokens);

            // 3. The repair pass later titles the recording. Before the ledger, this assignment
            //    erased step 2 outright.
            TitleBackfill.Apply(_dir, Named(100, 20));

            var cost = Loaded().AiCost!;
            Assert.Equal(500, cost.PromptTokens);
            Assert.Equal(110, cost.CompletionTokens);
            Assert.Contains(Translator.Model, cost.Model);
            Assert.Contains("title-model", cost.Model);
            Assert.Contains("610 tokens", cost.Basis);
        }

        [Fact]
        public void Translation_ThenPackagingTitle_KeepsBothCosts()
        {
            Translator.WriteTranslatedVtt(_dir, "es", Cues, new AiUsage(400, 90));
            Package.FinalizeManifest(_dir, Array.Empty<WalkthroughShot>(), Named(100, 20));

            var cost = Loaded().AiCost!;
            Assert.Equal(500, cost.PromptTokens);
            Assert.Equal(110, cost.CompletionTokens);
        }

        [Fact]
        public void PackagingTitle_ThenTranslation_KeepsBothCosts()
        {
            // The other completion order: whichever finishes last must still add, not assign.
            Package.FinalizeManifest(_dir, Array.Empty<WalkthroughShot>(), Named(100, 20));
            Translator.WriteTranslatedVtt(_dir, "es", Cues, new AiUsage(400, 90));

            var cost = Loaded().AiCost!;
            Assert.Equal(500, cost.PromptTokens);
            Assert.Equal(110, cost.CompletionTokens);
        }

        [Fact]
        public void VideoImportFinalize_AddsToTheCostAlreadyRecorded()
        {
            Translator.WriteTranslatedVtt(_dir, "es", Cues, new AiUsage(400, 90));
            VideoImport.WriteArtifacts(_dir, Cues, Named(100, 20));

            var cost = Loaded().AiCost!;
            Assert.Equal(500, cost.PromptTokens);
            Assert.Equal(110, cost.CompletionTokens);
        }

        [Fact]
        public void ARepeatedTitle_DoesNotNameTheSameModelTwice()
        {
            TitleBackfill.Apply(_dir, Named(10, 2));
            TitleBackfill.Apply(_dir, Named(10, 2));

            var cost = Loaded().AiCost!;
            Assert.Equal("title-model", cost.Model);   // named once, not "title-model, title-model"
            Assert.Equal(20, cost.PromptTokens);       // but both calls are counted
        }

        // ---- the ledger itself ----

        [Fact]
        public void Add_OntoNothing_IsTheCallsOwnUsage()
        {
            var cost = AiCostLedger.Add(existing: null, new AiUsage(100, 20), "m");

            Assert.Equal(100, cost.PromptTokens);
            Assert.Equal(20, cost.CompletionTokens);
            Assert.Equal("m", cost.Model);
            Assert.False(cost.IsEstimate);
            Assert.Contains("120 tokens", cost.Basis);
        }

        [Fact]
        public void Add_OntoAnExistingCost_Sums()
        {
            var existing = new AiCostInfo
            {
                Model = "title-model", PromptTokens = 100, CompletionTokens = 20,
                IsEstimate = false, Basis = "DevThrottle usage (120 tokens)",
            };
            var cost = AiCostLedger.Add(existing, new AiUsage(50, 10), Translator.Model);

            Assert.Equal(150, cost.PromptTokens);
            Assert.Equal(30, cost.CompletionTokens);
            Assert.Equal($"title-model, {Translator.Model}", cost.Model);
            Assert.False(cost.IsEstimate);
            Assert.Contains("180 tokens", cost.Basis);
        }

        [Fact]
        public void Add_NoUsageReported_MarksTheWholeTotalAnEstimate()
        {
            var cost = AiCostLedger.Add(existing: null, usage: null, model: Translator.Model);

            Assert.True(cost.IsEstimate);
            Assert.Equal(0, cost.PromptTokens);
            Assert.Equal(Translator.Model, cost.Model);
            Assert.Equal("DevThrottle (no usage reported)", cost.Basis);
        }

        [Fact]
        public void Add_OnceEstimated_StaysEstimated()
        {
            var estimated = AiCostLedger.Add(existing: null, usage: null, model: "m1");
            var cost = AiCostLedger.Add(estimated, new AiUsage(10, 2), "m2");

            Assert.True(cost.IsEstimate);   // a total containing an unmeasured call is not measured
        }

        // ---- nothing may build one any other way ----

        [Fact]
        public void NothingButTheLedger_ConstructsAnAiCost()
        {
            // The guard that covers the writer this test cannot drive: TitleBackfill.TitleAsync needs
            // a network round trip, so its Apply is exercised above, and this pins that no producer
            // anywhere in the product goes back to assigning a fresh cost over the running total.
            var offenders = ManifestWriterTests.ProductionSources()
                .Where(f => !f.EndsWith("Ai/AiCostLedger.cs", StringComparison.Ordinal))
                .Where(f => Regex.IsMatch(ManifestWriterTests.CodeOf(f), @"new\s+(AgentEyes\.)?(Ai\.)?AiCostInfo"))
                .ToList();

            Assert.Empty(offenders);
        }
    }
}
