using System;
using System.IO;

namespace AgentEyes.Preview
{
    /// <summary>
    /// Where a live preview frame is published (issue #33).
    ///
    /// DELIBERATELY OUTSIDE THE RECORDING DIRECTORY. A preview frame is a monitor, not an artifact:
    /// it is overwritten ten times a second, it is meaningless once the recording stops, and the
    /// Library, the repair passes and the packaging sequence all walk recording directories looking
    /// for files that mean something. Publishing into the recording would change what a recording IS
    /// (issue #33, AC11) for a file nobody keeps. It lives beside the logs and the config instead.
    /// </summary>
    internal static class PreviewPaths
    {
        /// <summary>The screen track's name, used for its frame file and in every log line.</summary>
        public const string ScreenTrack = "screen";

        /// <summary>The camera track's name.</summary>
        public const string CameraTrack = "camera";

        /// <summary>%LOCALAPPDATA%\AgentEyes\preview</summary>
        public static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentEyes", "preview");

        /// <summary>The published frame for one track ("screen", "camera").</summary>
        public static string Frame(string track)
        {
            if (string.IsNullOrWhiteSpace(track))
                throw new ArgumentException("a preview track must be named", nameof(track));
            return Path.Combine(Dir, track + ".jpg");
        }
    }
}
