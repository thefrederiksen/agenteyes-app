using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace AgentEyes.Audio
{
    /// <summary>What Windows says about a capture endpoint (issue #77): its name, whether it is muted,
    /// and its master volume in percent.</summary>
    internal readonly record struct MicEndpointState(string Name, bool Muted, double VolumePercent)
    {
        /// <summary>One line for the history and the log: "Headset Microphone: MUTED, volume 80%".</summary>
        public string Describe() => $"{Name}: {(Muted ? "MUTED" : "not muted")}, volume {VolumePercent:0}%";
    }

    /// <summary>
    /// Reads a capture endpoint's mute state and volume (IAudioEndpointVolume, through NAudio's
    /// MMDevice) - issue #77. This is the ONLY reliable sign of a muted microphone: a muted endpoint
    /// still delivers samples (near-silence, -96.7 dBFS on the owner's machine), so nothing in the
    /// measured level can tell "muted" from "quiet".
    /// </summary>
    internal static class MicEndpoint
    {
        /// <summary>
        /// The state of the active capture endpoint <paramref name="nameFragment"/> names (see
        /// <see cref="Select"/>: the exact friendly name, else the ONE name it is a prefix of - a WaveIn
        /// name is a 31-character prefix of the friendly name), or of Windows' default microphone when
        /// the fragment is null. Throws a <see cref="UsageException"/> naming the problem when there is
        /// no such device or more than one could be meant - never a guess at another one.
        /// </summary>
        public static MicEndpointState Read(string? nameFragment)
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice device;
            if (string.IsNullOrWhiteSpace(nameFragment))
            {
                if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
                    throw new UsageException("no default microphone is set in Windows (Settings > System > Sound > Input).");
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
            else
            {
                device = Find(enumerator, nameFragment.Trim());
            }
            using (device)
            {
                var volume = device.AudioEndpointVolume;
                var state = new MicEndpointState(device.FriendlyName, volume.Mute, Math.Round(volume.MasterVolumeLevelScalar * 100.0));
                Log.Info($"[MicEndpoint] Read: \"{nameFragment ?? "(default)"}\" -> {state.Describe()} (id {device.ID})");
                return state;
            }
        }

        /// <summary>
        /// Which of the active capture endpoints' friendly <paramref name="names"/> the configured
        /// <paramref name="fragment"/> means (case-insensitive) - the selection rule, pure so it is tested:
        ///  1. exactly one name EQUAL to the fragment -> that one;
        ///  2. else exactly one name that STARTS WITH the fragment (the WaveIn 31-character prefix) -> that one;
        ///  3. else a <see cref="UsageException"/>: none matched, or more than one could be meant (two
        ///     devices both called "Microphone (USB Audio)", or two whose names begin with "Microphone") -
        ///     the candidates are named so the owner can pick; nothing is guessed (the no-fallback rule).
        /// </summary>
        /// <returns>The index into <paramref name="names"/> of the one endpoint meant.</returns>
        public static int Select(string fragment, IReadOnlyList<string> names)
        {
            if (string.IsNullOrWhiteSpace(fragment)) throw new ArgumentException("the microphone name to look for is empty", nameof(fragment));
            if (names == null) throw new ArgumentNullException(nameof(names));
            string wanted = fragment.Trim();
            string active = names.Count == 0 ? "(none)" : string.Join("; ", names);

            var exact = new List<int>();
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], wanted, StringComparison.OrdinalIgnoreCase)) exact.Add(i);
            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1)
                throw new UsageException($"{exact.Count} active microphones are both named \"{wanted}\" - Windows cannot tell them apart by name, "
                                         + $"so the mute state cannot be read for one of them. Active: {active}.");

            var prefix = new List<int>();
            for (int i = 0; i < names.Count; i++)
                if (names[i].StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) prefix.Add(i);
            if (prefix.Count == 1) return prefix[0];
            if (prefix.Count > 1)
            {
                var candidates = new List<string>(prefix.Count);
                foreach (int i in prefix) candidates.Add("\"" + names[i] + "\"");
                throw new UsageException($"\"{wanted}\" could mean {prefix.Count} active microphones: {string.Join(", ", candidates)}. "
                                         + "Use the full device name in the setup so exactly one is meant.");
            }
            throw new UsageException($"no active microphone matches \"{wanted}\". Active: {active}.");
        }

        private static MMDevice Find(MMDeviceEnumerator enumerator, string fragment)
        {
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            var devices = new List<MMDevice>(endpoints.Count);
            var names = new List<string>(endpoints.Count);
            for (int i = 0; i < endpoints.Count; i++)
            {
                devices.Add(endpoints[i]);
                names.Add(endpoints[i].FriendlyName);
            }
            int chosen;
            try
            {
                chosen = Select(fragment, names);
            }
            catch (UsageException ex)
            {
                foreach (var d in devices) d.Dispose();
                Log.Warn($"[MicEndpoint] Find: \"{fragment}\" -> {ex.Message}");
                throw;
            }
            for (int i = 0; i < devices.Count; i++)
                if (i != chosen) devices[i].Dispose();
            Log.Info($"[MicEndpoint] Find: \"{fragment}\" -> \"{names[chosen]}\" (of {names.Count} active)");
            return devices[chosen];
        }
    }
}
