using System;

namespace AgentEyes.Audio
{
    /// <summary>
    /// The level of one captured audio buffer: its peak, and the sum of its squared samples so that
    /// buffers can be added up into a per-second RMS (issue #72). Samples are linear, 0..1 full scale.
    /// </summary>
    internal readonly record struct AudioLevel(float Peak, double SumSquares, int Samples)
    {
        /// <summary>The mean of the squared samples, or 0 for an empty buffer.</summary>
        public double MeanSquare => Samples > 0 ? SumSquares / Samples : 0.0;

        /// <summary>A buffer of <paramref name="samples"/> samples whose RMS is <paramref name="rmsDb"/>
        /// dBFS and whose peak is <paramref name="peakDb"/> dBFS. For tests and replays of measured
        /// levels, where only the two numbers are known.</summary>
        public static AudioLevel FromDb(double rmsDb, double peakDb, int samples)
        {
            if (samples <= 0) throw new ArgumentOutOfRangeException(nameof(samples), "a buffer has at least one sample");
            double ms = Math.Pow(10, rmsDb / 10.0);
            return new AudioLevel((float)Math.Pow(10, peakDb / 20.0), ms * samples, samples);
        }
    }
}
