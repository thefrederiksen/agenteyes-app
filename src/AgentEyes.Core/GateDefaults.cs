using System;

namespace AgentEyes
{
    /// <summary>
    /// Issue #83: the noise gate's only job is taming low-level speaker bleed / room noise. A
    /// mic-only source has no speaker bleed for it to tame, and an absolute gate there only risks
    /// cutting real (soft / sentence-end) speech - so the gate defaults OFF for a mic-only source
    /// and ON for mixed/system. Callers honor an explicit user override on top of this default.
    /// </summary>
    internal static class GateDefaults
    {
        /// <summary>The effective gate default for a capture source kind.</summary>
        public static bool For(AudioSourceKind src) => src != AudioSourceKind.Mic;

        /// <summary>The effective gate default for a preset source string ("mic" | "system" | "mixed").</summary>
        public static bool For(string? source) =>
            !string.Equals(source, "mic", StringComparison.OrdinalIgnoreCase);
    }
}
