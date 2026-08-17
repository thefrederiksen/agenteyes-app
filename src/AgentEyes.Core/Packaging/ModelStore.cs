using System;
using System.IO;
using System.Threading.Tasks;
using Whisper.net.Ggml;

namespace AgentEyes.Packaging
{
    /// <summary>
    /// Locates / downloads the Whisper GGML model. Cached under %LOCALAPPDATA%/AgentEyes/models.
    /// Download-once with a clear error if it fails - no silent fallback to "no transcript".
    /// </summary>
    internal static class ModelStore
    {
        public static GgmlType DefaultType => GgmlType.Base;

        /// <summary>Pure: where a given model type is cached.</summary>
        public static string PathFor(GgmlType type)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentEyes", "models");
            return Path.Combine(dir, $"ggml-{type.ToString().ToLowerInvariant()}.bin");
        }

        public static async Task<string> EnsureAsync(GgmlType type)
        {
            string path = PathFor(type);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return path;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Console.WriteLine($"  downloading Whisper model ({type}) - one time ...");

            try
            {
                using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type);
                using var fileWriter = File.OpenWrite(path);
                await modelStream.CopyToAsync(fileWriter);
            }
            catch (Exception ex)
            {
                if (File.Exists(path)) { try { File.Delete(path); } catch { } }
                throw new UsageException(
                    $"failed to download Whisper model ({type}): {ex.Message}. Check connectivity, then retry.");
            }

            return path;
        }
    }
}
