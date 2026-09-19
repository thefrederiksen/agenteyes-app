using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AgentEyes
{
    /// <summary>
    /// A READ-ONLY view, from Core, of the few App config values Core needs to produce things that
    /// reference the running application (issue #59).
    ///
    /// The App's <c>Config</c> class OWNS this file and is its only writer. This class reads it
    /// tolerantly - a missing file, a missing key or an unparsable value yields the same default the
    /// App uses - so a fresh machine or a half-written config never breaks packaging. The defaults
    /// here are a MIRROR of the App's: if either changes, the other must follow, and a test pins that.
    /// </summary>
    internal static class LocalAppConfig
    {
        /// <summary>The App's default port (Config.Port). Mirrored - see the class comment.</summary>
        internal const int DefaultPort = 7882;

        /// <summary>The App's default for extracting walkthrough frames to disk (issue #59).</summary>
        internal const bool DefaultWalkthroughExtractFrames = false;

        private static readonly Lazy<(int Port, bool ExtractFrames)> _values = new(Read);

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentEyes", "config.json");

        private static (int Port, bool ExtractFrames) Read()
        {
            try
            {
                if (!File.Exists(FilePath)) return (DefaultPort, DefaultWalkthroughExtractFrames);
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                var root = doc.RootElement;

                int port = DefaultPort;
                if (root.TryGetProperty("Port", out var p) && p.ValueKind == JsonValueKind.Number
                    && p.TryGetInt32(out int parsed) && parsed > 0)
                {
                    port = parsed;
                }

                bool extract = DefaultWalkthroughExtractFrames;
                if (root.TryGetProperty("WalkthroughExtractFrames", out var e)
                    && e.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    extract = e.GetBoolean();
                }

                return (port, extract);
            }
            catch
            {
                // A config this code cannot read is a reason to use the defaults, never a reason to
                // fail a recording's packaging.
                return (DefaultPort, DefaultWalkthroughExtractFrames);
            }
        }

        /// <summary>The port the local control API listens on.</summary>
        public static int Port => _values.Value.Port;

        /// <summary>True when walkthrough frames are extracted to disk; false when they are served on demand.</summary>
        public static bool WalkthroughExtractFrames => _values.Value.ExtractFrames;

        /// <summary>The base URL of the local control API.</summary>
        public static string Base => $"http://127.0.0.1:{Port}/";

        /// <summary>
        /// The URL a walkthrough page uses to ask for one frame of one recording at one offset,
        /// extracted from the video on demand (issue #59).
        /// </summary>
        public static string FrameUrl(string recordingId, double offsetSeconds) =>
            $"{Base}recordings/{Uri.EscapeDataString(recordingId)}/frame/"
            + offsetSeconds.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
