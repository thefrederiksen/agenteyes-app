using System.Speech.Synthesis;

namespace AgentEyes.Packaging
{
    /// <summary>
    /// Generates spoken-word WAV audio with known text (Windows TTS). Used by the self-test to feed
    /// the transcription pipeline a deterministic input whose words we can assert on.
    /// </summary>
    internal static class SpeechGen
    {
        public static void ToWav(string text, string wavPath)
        {
            using var synth = new SpeechSynthesizer();
            synth.SetOutputToWaveFile(wavPath);
            synth.Speak(text);
        }
    }
}
