using System;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Opt-in for tests that launch a REAL ffmpeg (issue #146). The default gate - dotnet test after
    /// every change, on three platforms - must stay fast and must never hang, and a test that runs a
    /// real encoder is neither; CI also has no microphone, so a behavioural case there proves less than
    /// it looks. These tests are therefore SKIPPED, visibly and with this reason, unless
    /// AGENTEYES_FFMPEG_TESTS=1 is set - the same explicit opt-in the heavy smokes use. The ORDER guard
    /// that does run in the gate is tests/Shared/MicChainOrderTests.cs.
    /// </summary>
    internal static class FfmpegOptIn
    {
        public const string Variable = "AGENTEYES_FFMPEG_TESTS";

        public static string? SkipReason =>
            Environment.GetEnvironmentVariable(Variable) == "1"
                ? null
                : $"launches a real ffmpeg - opt in with {Variable}=1 (issue #146: kept out of the default gate)";
    }

    /// <summary>A <see cref="FactAttribute"/> that runs only when <see cref="FfmpegOptIn.Variable"/> is 1.</summary>
    public sealed class FfmpegOptInFactAttribute : FactAttribute
    {
        public FfmpegOptInFactAttribute() { Skip = FfmpegOptIn.SkipReason; }
    }

    /// <summary>A <see cref="TheoryAttribute"/> that runs only when <see cref="FfmpegOptIn.Variable"/> is 1.</summary>
    public sealed class FfmpegOptInTheoryAttribute : TheoryAttribute
    {
        public FfmpegOptInTheoryAttribute() { Skip = FfmpegOptIn.SkipReason; }
    }
}
