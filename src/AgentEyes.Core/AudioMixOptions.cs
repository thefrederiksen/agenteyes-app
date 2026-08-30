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

        /// <summary>Apply a noise gate to the mic to tame low-level speaker bleed / room noise.
        /// This is the PERSON'S choice of whether to gate at all; how hard to gate is measured,
        /// never chosen here (see <see cref="GateThresholdLinear"/>).</summary>
        public bool NoiseGate { get; set; } = true;

        /// <summary>
        /// The gate threshold as a linear amplitude, MEASURED from the capture by
        /// <see cref="Audio.GateCalibration"/> - not a setting and not a constant.
        ///
        /// Null means one of two different things, and <see cref="GateCalibrated"/> is what tells
        /// them apart: no measurement has been taken yet (building the filter chain in that state
        /// is a programming error and throws), or a measurement was taken and found no room for a
        /// gate between the noise floor and the voice, in which case this take is not gated.
        ///
        /// It replaced a hardcoded 0.02 (-34 dBFS) that was applied to every microphone regardless
        /// of level; on a quiet mic that threshold sat above the speech and cut it to pieces.
        /// </summary>
        public double? GateThresholdLinear { get; set; }

        /// <summary>True once the capture has actually been measured for this run.</summary>
        public bool GateCalibrated { get; set; }

        /// <summary>Voice leveling on the mic (ffmpeg speechnorm) - boosts quiet speech and evens out
        /// volume so the listener never rides their volume knob.</summary>
        public bool VoiceLeveling { get; set; } = true;

        /// <summary>Path to the RNNoise model file (.rnnn) on disk. Must be set (via
        /// RnnoiseModel.Ensure) before building filter args when NoiseSuppression is on.</summary>
        public string? RnnoiseModelPath { get; set; }

        /// <summary>True when the mic track needs any post-capture processing at all.</summary>
        public bool MicProcessing => NoiseSuppression || NoiseGate || VoiceLeveling || MicGain != 1.0;

        /// <summary>True when the chain will actually emit a gate stage: the person asked for one
        /// AND the measurement found a threshold that clears the noise without touching the voice.</summary>
        public bool GateApplies => NoiseGate && GateThresholdLinear != null;

        /// <summary>Human-readable account of what the gate decided, for logs and the console.</summary>
        public string GateDescription()
        {
            if (!NoiseGate) return "gate off";
            if (!GateCalibrated) return "gate on (not yet measured)";
            return GateThresholdLinear == null
                ? "gate off (measured: no room between the noise floor and the voice)"
                : $"gate at {Audio.GateCalibration.ToDb(GateThresholdLinear.Value):0.0} dBFS (measured)";
        }
    }
}
