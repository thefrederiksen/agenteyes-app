using System;
using System.Collections.Generic;

namespace AgentEyes.Platform.Mac
{
    /// <summary>
    /// System audio (what the Mac is PLAYING) for a --mix or --loopback recording.
    ///
    /// THIS IS THE ONE THING macOS DOES NOT GIVE US. Windows has WASAPI loopback built into the
    /// operating system and Linux has the pulse monitor of the default sink. macOS has neither: there
    /// is no AVFoundation input that carries the system output, by design. The only way to record it
    /// is a virtual audio device that presents the output back as an input, and that is a signed
    /// kernel-level driver the USER installs and consents to - AgentEyes cannot and should not
    /// install one.
    ///
    /// So this class does exactly two things: it finds such a device if the user already has one, and
    /// it fails with the exact fix steps if not. It never silently records silence, and it never
    /// quietly substitutes the microphone - either of which would produce a recording that looks
    /// complete and has lost the thing it was made for.
    /// </summary>
    internal static class MacSystemAudio
    {
        /// <summary>
        /// The virtual loopback drivers we recognise, by the name fragment they present to
        /// AVFoundation. Order is preference order. These are the devices people actually install on
        /// a Mac for this job; a name not on this list can still be used with --mic, by its own name.
        /// </summary>
        internal static readonly string[] KnownDrivers =
        {
            "BlackHole",      // open source, the usual choice
            "Loopback",       // Rogue Amoeba Loopback
            "Soundflower",    // older, still in use
            "Existential",    // Existential Audio's own naming on some builds
            "Aggregate",      // a user-made aggregate device wrapping one of the above
        };

        /// <summary>
        /// Pick the system-audio input out of a listing. Pure, so the choice and the refusal are both
        /// unit-tested without any driver installed.
        /// </summary>
        internal static AvfDevice? Find(IReadOnlyList<AvfDevice> audioInputs, out string why)
        {
            foreach (string driver in KnownDrivers)
            {
                foreach (var d in audioInputs)
                {
                    if (d.Name.Contains(driver, StringComparison.OrdinalIgnoreCase))
                    {
                        why = "";
                        return d;
                    }
                }
            }
            why = audioInputs.Count == 0
                ? "this Mac lists no audio input at all."
                : "none of the audio inputs on this Mac is a loopback device that carries the system output.";
            return null;
        }

        /// <summary>
        /// The AVFoundation audio input that carries system sound. Fails with the exact fix steps
        /// rather than guessing - see the class comment for why there is nothing to guess at.
        /// </summary>
        public static AvfDevice Require()
        {
            var listing = AvfDevices.Read();
            var hit = Find(listing.Audio, out string why);
            if (hit == null)
            {
                throw new UsageException(
                    "system audio cannot be recorded on this Mac: " + why + " "
                    + "macOS has no built-in way to record what it is playing - unlike Windows and "
                    + "Linux, it needs a virtual audio device. Install one (BlackHole is free: "
                    + "'brew install blackhole-2ch'), set it as an output in a Multi-Output Device so "
                    + "you can still hear sound, then run 'agenteyes screens' to confirm AgentEyes "
                    + "sees it. Until then, record the microphone with --mic instead of --mix.");
            }
            Log.Info($"[MacSystemAudio] Require: :{hit.Value.Index} ({hit.Value.Name})");
            return hit.Value;
        }
    }
}
