using System;

namespace AgentEyes.Audio
{
    /// <summary>
    /// What a measurement of a captured microphone track says about its levels, in dBFS.
    ///
    /// Both numbers come from one ffmpeg <c>astats</c> pass over the WHOLE captured file (see
    /// <see cref="MicMeasure"/>), which is possible because the mic chain is a POST-capture pass -
    /// nothing here could work on a live stream, and it does not have to.
    /// </summary>
    internal readonly struct MicLevels
    {
        public MicLevels(double noiseFloorDb, double rmsDb)
        {
            NoiseFloorDb = noiseFloorDb;
            RmsDb = rmsDb;
        }

        /// <summary>The quietest sustained level in the take - the room, the fan, the hiss.</summary>
        public double NoiseFloorDb { get; }

        /// <summary>
        /// The RMS level of the whole track. It is a DELIBERATELY CONSERVATIVE proxy for "how loud
        /// is the speech": it includes the pauses, so it reads LOWER than the speech itself, which
        /// pushes the computed threshold DOWN and makes the gate gentler rather than harsher. That
        /// direction of error is the safe one - the defect this calibration exists to fix was a
        /// threshold that was too HIGH.
        /// </summary>
        public double RmsDb { get; }

        public override string ToString() =>
            $"noise floor {NoiseFloorDb:0.0} dBFS, RMS {RmsDb:0.0} dBFS";
    }

    /// <summary>
    /// Chooses the microphone noise gate's threshold FROM THE SIGNAL rather than from a constant.
    ///
    /// The defect this replaces: the gate threshold was the fixed literal 0.02 (-34 dBFS) for every
    /// microphone, every room and every speaking level. On a quiet microphone that sits ABOVE the
    /// speaker's voice and the gate eats the speech it was meant to clean. On one measured 162s
    /// take (noise floor -87.9 dBFS, RMS -38.1 dBFS) the fixed threshold was about 4 dB ABOVE the
    /// average speech level, 53% of the take fell under it, and the gate alone added 19 seconds of
    /// new silence.
    ///
    /// The rule has two edges and the gate must clear BOTH:
    ///
    ///  - it must sit <see cref="FloorMarginDb"/> ABOVE the measured noise floor, or it gates
    ///    nothing and is pointless;
    ///  - it must stay <see cref="SpeechHeadroomDb"/> BELOW the measured level, or it gates speech
    ///    and is harmful.
    ///
    /// When the measured floor and level are too close together for both to hold, there is no
    /// honest threshold and this returns null - the gate is switched OFF for that take and the
    /// reason is logged. It does NOT fall back to a guessed number: a made-up threshold is exactly
    /// the bug being fixed here, and a gate that silently damages audio is worse than no gate.
    /// </summary>
    internal static class GateCalibration
    {
        /// <summary>How far above the measured noise floor the gate sits.</summary>
        public const double FloorMarginDb = 10.0;

        /// <summary>How far below the measured level the gate must stay, always.</summary>
        public const double SpeechHeadroomDb = 12.0;

        /// <summary>
        /// The span between floor and level that a gate needs in order to fit between them at all.
        /// Below this the two edges above cross and there is no threshold that satisfies both.
        /// </summary>
        public const double MinUsableSpanDb = FloorMarginDb + SpeechHeadroomDb;

        /// <summary>
        /// The gate threshold in dBFS for a measured take, or null when the take has no room for a
        /// gate and it should be switched off instead.
        /// </summary>
        public static double? ThresholdDb(MicLevels levels)
        {
            if (double.IsNaN(levels.NoiseFloorDb) || double.IsInfinity(levels.NoiseFloorDb)
                || double.IsNaN(levels.RmsDb) || double.IsInfinity(levels.RmsDb))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(levels), levels.ToString(),
                    "gate calibration needs finite measured levels in dBFS");
            }

            double span = levels.RmsDb - levels.NoiseFloorDb;
            if (span < MinUsableSpanDb) return null;

            // Inside the span both edges hold at once, so the lower edge is the answer: sit just
            // clear of the noise and leave every bit of headroom to the voice.
            return levels.NoiseFloorDb + FloorMarginDb;
        }

        /// <summary>
        /// The same decision expressed as the LINEAR amplitude ffmpeg's <c>agate=threshold=</c>
        /// wants, or null when the take should not be gated.
        /// </summary>
        public static double? ThresholdLinear(MicLevels levels)
        {
            double? db = ThresholdDb(levels);
            return db == null ? null : ToLinear(db.Value);
        }

        /// <summary>dBFS to linear amplitude (0 dBFS = 1.0).</summary>
        public static double ToLinear(double db) => Math.Pow(10.0, db / 20.0);

        /// <summary>Linear amplitude to dBFS. Zero and below is treated as digital silence.</summary>
        public static double ToDb(double linear) =>
            linear <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(linear);
    }
}
