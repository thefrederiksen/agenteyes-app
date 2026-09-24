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
        /// The state of the active capture endpoint whose friendly name starts with or contains
        /// <paramref name="nameFragment"/> (case-insensitive; a WaveIn name is a 31-character prefix
        /// of the friendly name), or of Windows' default microphone when the fragment is null. Throws
        /// a <see cref="UsageException"/> naming the problem when there is no such device - never a
        /// guess at another one.
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
                return new MicEndpointState(device.FriendlyName, volume.Mute, Math.Round(volume.MasterVolumeLevelScalar * 100.0));
            }
        }

        private static MMDevice Find(MMDeviceEnumerator enumerator, string fragment)
        {
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            MMDevice? match = null;
            var names = new List<string>(endpoints.Count);
            for (int i = 0; i < endpoints.Count; i++)
            {
                var endpoint = endpoints[i];
                names.Add(endpoint.FriendlyName);
                if (match == null && endpoint.FriendlyName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    match = endpoint;
                    continue;
                }
                endpoint.Dispose();
            }
            if (match == null)
                throw new UsageException($"no active microphone matches \"{fragment}\". Active: {(names.Count == 0 ? "(none)" : string.Join("; ", names))}.");
            return match;
        }
    }
}
