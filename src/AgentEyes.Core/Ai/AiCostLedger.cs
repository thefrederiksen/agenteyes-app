using System;
using System.Linq;

namespace AgentEyes.Ai
{
    /// <summary>
    /// The one way <see cref="Manifest.AiCost"/> is produced (issue #155).
    ///
    /// A recording's AI cost is a RUNNING TOTAL of everything the app spent on that recording - the
    /// manifest documents it as the per-recording spend - so every producer must ADD to what is
    /// already there. Before this, only the translator did: packaging, the title backfill and the
    /// video import each assigned a brand-new <see cref="AiCostInfo"/> over the top, and the earlier
    /// usage was gone.
    ///
    /// No concurrency was needed to lose data that way. Title generation fails (it is non-fatal by
    /// design), the user translates the transcript so a translation cost is recorded, the repair pass
    /// later titles the recording - and the assignment in the title path erased the translation
    /// usage. Making the write atomic and serialized (the rest of issue #155) does not help: both
    /// writes are legitimate and both land; the loss is in the value being written.
    ///
    /// Pure and total: no I/O, no clock, no exception path. The caller does the accumulate INSIDE its
    /// <see cref="ManifestStore.Update"/>, so the "current" value is the one on disk at that moment.
    /// </summary>
    internal static class AiCostLedger
    {
        /// <summary>
        /// Fold one AI call's token usage into the recording's running cost. <paramref name="existing"/>
        /// is what the manifest says now (null when nothing has been spent yet);
        /// <paramref name="usage"/> is null when the provider reported none, which makes the total an
        /// estimate from then on (<see cref="AiCostInfo.IsEstimate"/>).
        ///
        /// <see cref="AiCostInfo.Model"/> becomes the comma-separated set of models that contributed,
        /// each named once, so a recording titled by one model and translated by another says so.
        /// </summary>
        public static AiCostInfo Add(AiCostInfo? existing, AiUsage? usage, string model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            int prompt = (existing?.PromptTokens ?? 0) + (usage?.PromptTokens ?? 0);
            int completion = (existing?.CompletionTokens ?? 0) + (usage?.CompletionTokens ?? 0);
            bool isEstimate = (existing?.IsEstimate ?? false) || usage is null;

            string models;
            if (existing is null || string.IsNullOrEmpty(existing.Model)) models = model;
            else if (existing.Model.Split(new[] { ", " }, StringSplitOptions.None).Contains(model)) models = existing.Model;
            else models = existing.Model + ", " + model;

            return new AiCostInfo
            {
                Model = models,
                PromptTokens = prompt,
                CompletionTokens = completion,
                // DevThrottle bills at its own server-side rate card, so the client records the token
                // usage it was told about - never a dollar figure it cannot know (issue #88).
                CostUsd = 0,
                IsEstimate = isEstimate,
                Basis = isEstimate
                    ? "DevThrottle (no usage reported)"
                    : $"DevThrottle usage ({prompt + completion} tokens)",
            };
        }
    }
}
