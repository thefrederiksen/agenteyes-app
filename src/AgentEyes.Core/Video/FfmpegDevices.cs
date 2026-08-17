using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AgentEyes.Video
{
    /// <summary>Enumerate DirectShow audio devices via ffmpeg, plus a pure parser for its output.</summary>
    internal static class FfmpegDevices
    {
        /// <summary>
        /// Pure parser for the stderr of `ffmpeg -list_devices true -f dshow -i dummy`.
        /// Audio device lines look like:  [dshow @ ...] "Microphone (Yeti)" (audio)
        /// </summary>
        public static IReadOnlyList<string> ParseDshowAudio(string ffmpegStderr)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(ffmpegStderr)) return names;

            // Match a quoted name followed (on the same or next line) by an "(audio)" marker.
            var rx = new Regex("\"([^\"]+)\"\\s*\\(audio\\)", RegexOptions.IgnoreCase);
            foreach (Match m in rx.Matches(ffmpegStderr))
            {
                string name = m.Groups[1].Value;
                if (!names.Contains(name)) names.Add(name);
            }

            // Newer ffmpeg puts "(audio)" on the line below the name with an alternative_name line in between.
            if (names.Count == 0)
            {
                var lines = ffmpegStderr.Replace("\r", "").Split('\n');
                var nameRx = new Regex("\"([^\"]+)\"");
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].IndexOf("(audio)", StringComparison.OrdinalIgnoreCase) >= 0)
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

        public static IReadOnlyList<string> ListAudio()
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
            return ParseDshowAudio(stderr);
        }
    }
}
