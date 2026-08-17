using System;
using System.IO;
using System.Reflection;

namespace AgentEyes.Audio
{
    /// <summary>
    /// Ships the RNNoise speech-denoising model (for ffmpeg's arnndn filter) embedded in the
    /// executable and materializes it on disk at first use - ffmpeg can only read the model from
    /// a file. Model: "beguiling-drafter" from github.com/GregorR/rnnoise-models (BSD licensed).
    /// </summary>
    internal static class RnnoiseModel
    {
        private const string ResourceName = "AgentEyes.Audio.bd.rnnn";
        private static string? _cache;

        /// <summary>Returns the on-disk path of the model, extracting it if needed.
        /// Throws with exact guidance if the embedded resource is missing - no silent skip.</summary>
        public static string Ensure()
        {
            if (_cache != null && File.Exists(_cache)) return _cache;

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentEyes", "models");
            string path = Path.Combine(dir, "bd.rnnn");

            using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new UsageException(
                    $"RNNoise model resource '{ResourceName}' missing from this build. " +
                    "Rebuild AgentEyes.Core (assets\\models\\bd.rnnn must exist in the repo).");

            // (Re)write if absent or a different size (e.g. interrupted extract, model upgrade).
            if (!File.Exists(path) || new FileInfo(path).Length != src.Length)
            {
                Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                using (var dst = File.Create(tmp)) src.CopyTo(dst);
                File.Move(tmp, path, overwrite: true);
            }
            return _cache = path;
        }
    }
}
