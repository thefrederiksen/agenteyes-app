namespace AgentEyes
{
    /// <summary>Settings for combining the microphone and system-audio streams into one track.</summary>
    internal sealed class AudioMixOptions
    {
        /// <summary>Mic gain multiplier (1.0 = unchanged).</summary>
        public double MicGain { get; set; } = 1.0;

        /// <summary>System-audio gain multiplier (often &lt;1 so narration sits above the program).</summary>
        public double SystemGain { get; set; } = 0.7;

        /// <summary>RNNoise neural noise suppression on the mic (ffmpeg arnndn) - removes steady
        /// background noise (fans, hum, hiss) while preserving speech. The same approach OBS uses.</summary>
        public bool NoiseSuppression { get; set; } = true;

        /// <summary>Apply a noise gate to the mic to tame low-level speaker bleed / room noise.</summary>
        public bool NoiseGate { get; set; } = true;

        /// <summary>Gate threshold as a linear amplitude 0..1 (below this the mic is attenuated).</summary>
        public double GateThreshold { get; set; } = 0.02;

        /// <summary>Voice leveling on the mic (ffmpeg speechnorm) - boosts quiet speech and evens out
        /// volume so the listener never rides their volume knob.</summary>
        public bool VoiceLeveling { get; set; } = true;

        /// <summary>Path to the RNNoise model file (.rnnn) on disk. Must be set (via
        /// RnnoiseModel.Ensure) before building filter args when NoiseSuppression is on.</summary>
        public string? RnnoiseModelPath { get; set; }

        /// <summary>True when the mic track needs any post-capture processing at all.</summary>
        public bool MicProcessing => NoiseSuppression || NoiseGate || VoiceLeveling || MicGain != 1.0;
    }
}
