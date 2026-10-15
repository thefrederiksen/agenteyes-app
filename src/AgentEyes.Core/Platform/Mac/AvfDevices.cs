using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AgentEyes.Video;

namespace AgentEyes.Platform.Mac
{
    /// <summary>One AVFoundation input as ffmpeg's device listing names it.</summary>
    /// <param name="Index">The number ffmpeg's "-i index:index" addresses this input by.</param>
    /// <param name="Name">The human name ffmpeg printed ("FaceTime HD Camera", "Capture screen 0").</param>
    internal readonly record struct AvfDevice(int Index, string Name)
    {
        /// <summary>A screen, not a camera: AVFoundation lists displays among the video devices.</summary>
        public bool IsScreen => Name.StartsWith("Capture screen", StringComparison.Ordinal);
    }

    /// <summary>
    /// The AVFoundation inputs on this Mac, listed through ffmpeg itself
    /// (<c>ffmpeg -f avfoundation -list_devices true -i ""</c>) so the indexes are exactly the ones
    /// the recording ffmpeg will open. This is the macOS counterpart of the Linux PulseDevices and
    /// V4l2Devices pair, and of DirectShow's device listing on Windows.
    ///
    /// AVFoundation puts DISPLAYS and CAMERAS in one numbered video list, so a camera's index moves
    /// when a monitor is plugged in. Nothing here caches an index: every listing is read fresh at the
    /// moment it is needed, which is also why a recording resolves its device immediately before it
    /// opens it.
    /// </summary>
    internal static class AvfDevices
    {
        // "[AVFoundation indev @ 0x7f8] [1] Capture screen 0"
        private static readonly Regex DeviceLine = new(
            @"^\[AVFoundation indev @ [^\]]+\]\s*\[(?<idx>\d+)\]\s*(?<name>.+?)\s*$", RegexOptions.Compiled);

        private static readonly Regex VideoHeader = new(
            @"^\[AVFoundation indev @ [^\]]+\]\s*AVFoundation video devices:", RegexOptions.Compiled);

        private static readonly Regex AudioHeader = new(
            @"^\[AVFoundation indev @ [^\]]+\]\s*AVFoundation audio devices:", RegexOptions.Compiled);

        /// <summary>The video (screen and camera) and audio inputs, in ffmpeg's own order.</summary>
        internal readonly record struct Listing(
            IReadOnlyList<AvfDevice> Video, IReadOnlyList<AvfDevice> Audio);

        /// <summary>
        /// Parse ffmpeg's AVFoundation device listing. Pure, so every shape of it is unit-tested
        /// without a Mac, a camera or a microphone attached.
        /// </summary>
        internal static Listing ParseListing(string output)
        {
            var video = new List<AvfDevice>();
            var audio = new List<AvfDevice>();
            if (string.IsNullOrEmpty(output)) return new Listing(video, audio);

            List<AvfDevice>? current = null;
            foreach (var raw in output.Replace("\r", "").Split('\n'))
            {
                if (VideoHeader.IsMatch(raw)) { current = video; continue; }
                if (AudioHeader.IsMatch(raw)) { current = audio; continue; }
                if (current == null) continue;
                var m = DeviceLine.Match(raw);
                if (!m.Success) continue;
                current.Add(new AvfDevice(int.Parse(m.Groups["idx"].Value), m.Groups["name"].Value));
            }
            return new Listing(video, audio);
        }

        /// <summary>
        /// Read the listing from ffmpeg. ffmpeg always exits non-zero after a device listing (it was
        /// asked to open an empty input), so the exit code is not the signal - an EMPTY listing is.
        /// An empty listing means ffmpeg answered without naming a single input, which on a Mac that
        /// certainly has a display means the listing failed, and that is reported rather than
        /// returned as "no devices".
        /// </summary>
        public static Listing Read()
        {
            var r = ToolRunner.Run(
                FfmpegLocator.Ffmpeg(),
                new[] { "-hide_banner", "-f", "avfoundation", "-list_devices", "true", "-i", "" },
                TimeSpan.FromSeconds(15));

            // ffmpeg prints the listing on stderr; both streams are joined so a future build that
            // prints it on stdout is read just the same.
            var listing = ParseListing(r.StdOut + "\n" + r.StdErr);
            if (listing.Video.Count == 0 && listing.Audio.Count == 0)
            {
                throw new UsageException(
                    "ffmpeg listed no AVFoundation inputs at all, so neither the screen nor any "
                    + "microphone could be found. This is what a missing Screen Recording permission "
                    + "looks like from here. Grant it in System Settings > Privacy and Security > "
                    + "Screen and System Audio Recording, to the application that launches AgentEyes, "
                    + "then try again. (ffmpeg exit " + r.ExitCode + ": " + Tail(r.StdErr) + ")");
            }
            Log.Info($"[AvfDevices] Read: {listing.Video.Count} video input(s), {listing.Audio.Count} audio input(s)");
            return listing;
        }

        /// <summary>The displays AVFoundation can capture, in listing order.</summary>
        public static IReadOnlyList<AvfDevice> Screens() => Read().Video.Where(d => d.IsScreen).ToList();

        /// <summary>The cameras: every video input that is not a display.</summary>
        public static IReadOnlyList<AvfDevice> Cameras() => Read().Video.Where(d => !d.IsScreen).ToList();

        /// <summary>The microphones, by the name a --mic fragment is matched against.</summary>
        public static IReadOnlyList<string> MicrophoneNames() => Read().Audio.Select(d => d.Name).ToList();

        /// <summary>
        /// The AVFoundation index of a microphone name that <see cref="DeviceResolver"/> already
        /// resolved. Fails when the device has gone since it was resolved (unplugged in between), and
        /// never falls back to another microphone.
        /// </summary>
        public static int RequireMicrophoneIndex(string name) =>
            MicrophoneIndexFor(Read().Audio, name)
            ?? throw new UsageException(
                $"the microphone \"{name}\" is no longer listed by AVFoundation. Run 'agenteyes screens'.");

        internal static int? MicrophoneIndexFor(IReadOnlyList<AvfDevice> audio, string name)
        {
            foreach (var d in audio)
            {
                if (string.Equals(d.Name, name, StringComparison.Ordinal)) return d.Index;
            }
            return null;
        }

        /// <summary>
        /// The AVFoundation index of a camera name that was already resolved, by exact name. The
        /// macOS counterpart of V4l2Devices.DevicePath - a camera is addressed by index here, and the
        /// index is re-read now because plugging in a display renumbers the video inputs.
        /// </summary>
        public static int RequireCameraIndex(string name)
        {
            foreach (var d in Cameras())
            {
                if (string.Equals(d.Name, name, StringComparison.Ordinal)) return d.Index;
            }
            throw new UsageException(
                $"the camera \"{name}\" is no longer listed by AVFoundation. Run 'agenteyes screens'.");
        }

        private static string Tail(string s)
        {
            s = (s ?? "").Trim();
            return s.Length <= 300 ? s : "..." + s[^300..];
        }
    }
}
