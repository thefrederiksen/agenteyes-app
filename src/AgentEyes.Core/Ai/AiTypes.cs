namespace AgentEyes.Ai
{
    /// <summary>Token counts the chat API reported for one request (the "usage" object).
    /// These are the real billed tokens, not an estimate.</summary>
    internal sealed record AiUsage(int PromptTokens, int CompletionTokens)
    {
        public int TotalTokens => PromptTokens + CompletionTokens;
    }

    /// <summary>
    /// Per-recording AI spend, serialized into manifest.json. AgentEyes runs on DevThrottle
    /// (issue #88): DevThrottle bills at its own server-side rate card, so the client records
    /// the token usage - not a dollar figure it cannot know. <see cref="IsEstimate"/> is true
    /// when the provider omitted the usage object.
    /// </summary>
    internal sealed class AiCostInfo
    {
        public string Model { get; set; } = "";
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public double CostUsd { get; set; }
        public bool IsEstimate { get; set; }
        /// <summary>How the number was arrived at, for the tooltip/detail view.</summary>
        public string? Basis { get; set; }
    }
}
