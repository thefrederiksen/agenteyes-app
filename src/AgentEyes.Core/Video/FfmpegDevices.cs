using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AgentEyes.Video
{
    /// <summary>
    /// Enumerate DirectShow capture devices via ffmpeg (audio inputs and video/camera inputs), plus
    /// pure parsers for its output.
    /// </summary>
    internal static class FfmpegDevices
    {
        /// <summary>
        /// Pure parser for the stderr of `ffmpeg -list_devices true -f dshow -i dummy`.
        /// Audio device lines look like:  [dshow @ ...] "Microphone (Yeti)" (audio)
        /// </summary>
        public static IReadOnlyList<string> ParseDshowAudio(string ffmpegStderr) =>
            ParseDshowKind(ffmpegStderr, "audio");

        /// <summary>
        /// Pure parser for the same listing, for VIDEO (camera) devices (issue #28).
        /// Video device lines look like:  [dshow @ ...] "HD Webcam" (video)
        ///
        /// This is the exact mirror of <see cref="ParseDshowAudio"/> - deliberately the SAME code
        /// path with a different marker rather than a second implementation, because ffmpeg prints
        /// both kinds in one listing and any difference between the two parsers would be a defect
        /// waiting to happen (a camera whose name contains the word "audio", a new ffmpeg layout
        /// fixed on one side only).
        /// </summary>
        public static IReadOnlyList<string> ParseDshowVideo(string ffmpegStderr) =>
            ParseDshowKind(ffmpegStderr, "video");

        /// <summary>
        /// Pull every device name carrying the "(audio)" or "(video)" marker out of one ffmpeg
        /// device listing, in listing order, without duplicates.
        /// </summary>
        private static IReadOnlyList<string> ParseDshowKind(string ffmpegStderr, string kind)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(ffmpegStderr)) return names;

            // Match a quoted name followed on the same line by the "(audio)"/"(video)" marker.
            var rx = new Regex("\"([^\"]+)\"\\s*\\(" + kind + "\\)", RegexOptions.IgnoreCase);
            foreach (Match m in rx.Matches(ffmpegStderr))
            {
                string name = m.Groups[1].Value;
                if (!names.Contains(name)) names.Add(name);
            }

            // Newer ffmpeg puts the marker on the line below the name with an alternative_name line
            // in between.
            if (names.Count == 0)
            {
                var lines = ffmpegStderr.Replace("\r", "").Split('\n');
                var nameRx = new Regex("\"([^\"]+)\"");
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].IndexOf("(" + kind + ")", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var nm = nameRx.Match(lines[i]);
                        if (nm.Success && !names.Contains(nm.Groups[1].Value))
                        {
                            names.Add(nm.Groups[1].Value);
                        }
                    }
                }
            }

            return names;
        }

        public static IReadOnlyList<string> ListAudio() => ParseDshowAudio(RunDeviceListing());

        /// <summary>
        /// The DirectShow VIDEO (camera) devices attached to this machine, by exact ffmpeg name
        /// (issue #28). The name returned here is what `-f dshow -i video=&lt;name&gt;` wants.
        /// An empty list means the machine has no camera - it is NOT an error.
        /// </summary>
        public static IReadOnlyList<string> ListVideo() => ParseDshowVideo(RunDeviceListing());

        /// <summary>
        /// Run ffmpeg's device listing once and return its stderr (where ffmpeg prints the list).
        /// One listing carries BOTH the audio and the video sections, so the two enumerators above
        /// share it rather than launching ffmpeg twice with different parsers.
        /// </summary>
        private static string RunDeviceListing()
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffmpeg(),
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return stderr;
        }
    }
}
