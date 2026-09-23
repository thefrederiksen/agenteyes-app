using System;
using System.Collections.Generic;

namespace AgentEyes
{
    /// <summary>
    /// Pure resolver: map a user-supplied microphone name fragment to a single device.
    /// No silent fallback to a default device - absent or ambiguous both throw.
    /// </summary>
    internal static class DeviceResolver
    {
        public static int Resolve(IReadOnlyList<(int Number, string Name)> devices, string fragment)
        {
            if (devices.Count == 0)
            {
                throw new UsageException("no microphone input devices found.");
            }
            if (string.IsNullOrWhiteSpace(fragment))
            {
                throw new UsageException("microphone name fragment is empty.");
            }

            int matchIndex = -1;
            int matchCount = 0;
            foreach (var (number, name) in devices)
            {
                if (name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matchIndex = number;
                    matchCount++;
                }
            }

            if (matchCount == 0)
            {
                throw new UsageException(
                    $"no microphone matches \"{fragment}\". Run 'agenteyes screens' to list devices.");
            }
            if (matchCount > 1)
            {
                throw new UsageException(
                    $"\"{fragment}\" matches {matchCount} devices. Use a more specific --mic name.");
            }
            return matchIndex;
        }

        /// <summary>
        /// Resolve to the matching device NAME (used by the ffmpeg dshow engine, which addresses
        /// microphones by exact name rather than index).
        /// </summary>
        public static string ResolveName(IReadOnlyList<string> deviceNames, string fragment)
        {
            if (deviceNames.Count == 0)
            {
                throw new UsageException("no DirectShow audio devices found (is ffmpeg present?).");
            }
            if (string.IsNullOrWhiteSpace(fragment))
            {
                throw new UsageException("microphone name fragment is empty.");
            }

            string? match = null;
            int matchCount = 0;
            foreach (var name in deviceNames)
            {
                if (name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    match = name;
                    matchCount++;
                }
            }

            if (matchCount == 0)
            {
                throw new UsageException(
                    $"no DirectShow microphone matches \"{fragment}\". Run 'agenteyes screens'.");
            }
            if (matchCount > 1)
            {
                throw new UsageException(
                    $"\"{fragment}\" matches {matchCount} DirectShow devices. Use a more specific --mic name.");
            }
            return match!;
        }

        /// <summary>
        /// Resolve a CAMERA name fragment to the one exact DirectShow video device it names
        /// (issue #28). Same no-silent-fallback contract as <see cref="ResolveName"/>: absent throws,
        /// ambiguous throws, and there is deliberately no "just take the first camera" path - a
        /// recording that quietly filmed the wrong lens, or quietly filmed nothing, is worse than one
        /// that refused to start (issue #28, decision 3).
        ///
        /// Every message names the fragment the caller asked for, because that is the thing the user
        /// has to change.
        /// </summary>
        public static string ResolveCameraName(IReadOnlyList<string> deviceNames, string fragment)
        {
            if (string.IsNullOrWhiteSpace(fragment))
            {
                throw new UsageException("camera name fragment is empty.");
            }
            if (deviceNames.Count == 0)
            {
                throw new UsageException(
                    $"no DirectShow camera matches \"{fragment}\" - this machine reports no cameras at all. " +
                    "Run 'agenteyes screens' to list devices.");
            }

            string? match = null;
            int matchCount = 0;
            foreach (var name in deviceNames)
            {
                if (name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    match = name;
                    matchCount++;
                }
            }

            if (matchCount == 0)
            {
                throw new UsageException(
                    $"no DirectShow camera matches \"{fragment}\". Run 'agenteyes screens' to list cameras.");
            }
            if (matchCount > 1)
            {
                throw new UsageException(
                    $"\"{fragment}\" matches {matchCount} DirectShow cameras. Use a more specific --camera name.");
            }
            return match!;
        }
    }
}
