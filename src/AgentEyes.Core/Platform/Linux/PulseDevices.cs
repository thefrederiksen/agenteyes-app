using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AgentEyes.Video;

namespace AgentEyes.Platform.Linux
{
    /// <summary>One PulseAudio endpoint as ffmpeg's pulse device lists it.</summary>
    /// <param name="Name">The pulse name ffmpeg's "-f pulse -i NAME" wants (alsa_input....).</param>
    /// <param name="Description">The human name ("Yealink UH36 Analog Stereo") - what is shown and matched.</param>
    /// <param name="IsDefault">Marked as the server's default in the listing.</param>
    internal sealed record PulseEndpoint(string Name, string Description, bool IsDefault)
    {
        /// <summary>A monitor source carries what a sink plays - system audio, not a microphone.</summary>
        public bool IsMonitor => Name.EndsWith(".monitor", StringComparison.Ordinal);
    }

    /// <summary>
    /// The PulseAudio protocol's microphones and outputs (issue #102), listed through ffmpeg itself
    /// (<c>ffmpeg -sources pulse</c> / <c>-sinks pulse</c>) so the names are exactly what the recording
    /// ffmpeg will open. On this machine the server is PipeWire's pulse layer; nothing here depends on
    /// which server answers.
    ///
    /// System audio is the MONITOR of the default sink (assumption A2): whatever the desktop is
    /// playing to the default output, resolved at the moment a recording starts.
    /// </summary>
    internal static class PulseDevices
    {
        // "* alsa_input.usb-Yealink-00.analog-stereo [Yealink UH36 Analog Stereo]"
        private static readonly Regex EndpointLine = new(
            @"^(?<def>[* ])\s(?<name>\S+)\s\[(?<desc>.*)\]\s*$", RegexOptions.Compiled);

        /// <summary>Parse ffmpeg's "-sources pulse" / "-sinks pulse" listing. Pure, unit-tested.</summary>
        internal static List<PulseEndpoint> ParseListing(string output)
        {
            var list = new List<PulseEndpoint>();
            if (string.IsNullOrEmpty(output)) return list;
            foreach (var raw in output.Replace("\r", "").Split('\n'))
            {
                var m = EndpointLine.Match(raw);
                if (!m.Success) continue;
                list.Add(new PulseEndpoint(m.Groups["name"].Value, m.Groups["desc"].Value.Trim(), m.Groups["def"].Value == "*"));
            }
            return list;
        }

        public static IReadOnlyList<PulseEndpoint> Sources() => List("-sources");

        public static IReadOnlyList<PulseEndpoint> Sinks() => List("-sinks");

        /// <summary>Microphones: every source that is not a sink monitor.</summary>
        public static IReadOnlyList<PulseEndpoint> Microphones() => Sources().Where(s => !s.IsMonitor).ToList();

        /// <summary>
        /// The microphone names a fragment is matched against (the descriptions), in listing order.
        /// The Linux face of <see cref="FfmpegDevices.ListAudio"/>.
        /// </summary>
        public static IReadOnlyList<string> MicrophoneNames() => Microphones().Select(m => m.Description).ToList();

        /// <summary>
        /// The pulse name for a microphone description that <see cref="DeviceResolver"/> already
        /// resolved. Fails when the device has gone since it was resolved (unplugged in between).
        /// </summary>
        public static string RequireSourceName(string description) =>
            SourceNameFor(Microphones(), description)
            ?? throw new UsageException($"the microphone \"{description}\" is no longer listed by the audio server. Run 'agenteyes screens'.");

        internal static string? SourceNameFor(IReadOnlyList<PulseEndpoint> mics, string description)
        {
            var hit = mics.FirstOrDefault(m => string.Equals(m.Description, description, StringComparison.Ordinal));
            return hit?.Name;
        }

        /// <summary>
        /// The source that carries system audio: the monitor of the default sink. Fails - never
        /// guesses another sink - when there is no default sink or its monitor is not listed.
        /// </summary>
        public static PulseEndpoint RequireSystemAudioSource()
        {
            var monitor = SystemAudioSource(Sinks(), Sources(), out string why);
            if (monitor == null) throw new UsageException("system audio cannot be recorded: " + why);
            Log.Info($"[PulseDevices] RequireSystemAudioSource: {monitor.Name} ({monitor.Description})");
            return monitor;
        }

        internal static PulseEndpoint? SystemAudioSource(
            IReadOnlyList<PulseEndpoint> sinks, IReadOnlyList<PulseEndpoint> sources, out string why)
        {
            var sink = sinks.FirstOrDefault(s => s.IsDefault);
            if (sink == null)
            {
                why = sinks.Count == 0 ? "the audio server lists no output (sink) at all." : "the audio server marks no output as the default.";
                return null;
            }
            string monitorName = sink.Name + ".monitor";
            var monitor = sources.FirstOrDefault(s => s.Name == monitorName);
            if (monitor == null)
            {
                why = $"the default output \"{sink.Description}\" has no monitor source ({monitorName}) listed.";
                return null;
            }
            why = "";
            return monitor;
        }

        private static IReadOnlyList<PulseEndpoint> List(string flag)
        {
            var r = ToolRunner.Run(FfmpegLocator.Ffmpeg(), new[] { "-hide_banner", flag, "pulse" }, TimeSpan.FromSeconds(10));
            // ffmpeg exits non-zero after a listing on some builds; the listing itself is the answer,
            // and an empty listing from a failed run is reported, not taken as "no devices".
            var list = ParseListing(r.StdOut + "\n" + r.StdErr);
            if (list.Count == 0 && r.ExitCode != 0)
                throw new UsageException($"ffmpeg could not list pulse {flag.TrimStart('-')} (exit {r.ExitCode}): {Tail(r.StdErr)}. "
                    + "Is the PulseAudio/PipeWire-pulse server running?");
            Log.Info($"[PulseDevices] List {flag}: {list.Count} endpoint(s)");
            return list;
        }

        private static string Tail(string s)
        {
            s = s.Trim();
            return s.Length <= 300 ? s : "..." + s[^300..];
        }
    }
}
