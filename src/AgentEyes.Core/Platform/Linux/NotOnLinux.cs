using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AgentEyes.Platform.Linux
{
    /// <summary>
    /// The one wording for a Windows-only feature reached on Linux (issue #102). These features are
    /// OUT of the Linux scope (always-on, the self-test's TTS voice, tones, the interactive region
    /// overlay, mute-state reads) - they fail loudly with this, never silently do nothing.
    /// </summary>
    internal static class NotOnLinux
    {
        public static PlatformNotSupportedException Feature(string feature)
        {
            string msg = $"{feature} is not available on Linux yet (issue #102 brings recording, the headless "
                + "Control API and the systemd service; this part of AgentEyes is still Windows-only).";
            Log.Error("[NotOnLinux] " + msg);
            return new PlatformNotSupportedException(msg);
        }
    }
}

namespace AgentEyes.Audio
{
    using AgentEyes.Platform.Linux;

    /// <summary>Windows' endpoint mute/volume for a microphone. Linux: not read (always-on only).</summary>
    internal readonly record struct MicEndpointState(string Name, bool Muted, double VolumePercent)
    {
        public string Describe() => $"{Name}: {(Muted ? "MUTED" : "not muted")}, volume {VolumePercent:0}%";
    }

    internal static class MicEndpoint
    {
        public static MicEndpointState Read(string? nameFragment) => throw NotOnLinux.Feature("Reading the microphone mute state");
    }

    internal static class DefaultMic
    {
        public static string FriendlyName() => throw NotOnLinux.Feature("Picking the default microphone for always-on");
    }

    internal sealed class TonePlayer : IDisposable
    {
        public void Play(string wavPath) => throw NotOnLinux.Feature("Playing test tones");
        public void Stop() { }
        public void Dispose() { }
    }
}

namespace AgentEyes.AlwaysOn
{
    using AgentEyes.Platform.Linux;

    internal static class KillOnCloseJob
    {
        public static void Assign(Process process) => throw NotOnLinux.Feature("Always-on recording");
    }

    /// <summary>Always-on's piece recorder. Linux: always-on is out of scope, so it never starts.</summary>
    internal sealed class ContinuousRecorder : IDisposable
    {
        public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(10);
        public string CommandLine => "";
        public string Encoder => "";
        public DateTime StartedUtc => default;
        public bool HasExited => false;
        public string StderrTail => "";
        public string ProcessState => "not started";
        public static string ChooseEncoder() => throw NotOnLinux.Feature("Always-on recording");
        public void Start(AlwaysOnOptions o, SoundLog sound) => throw NotOnLinux.Feature("Always-on recording");
        public void Stop() { }
        public void Dispose() { }
    }
}

namespace AgentEyes.Packaging
{
    using AgentEyes.Platform.Linux;

    internal static class SpeechGen
    {
        public static void ToWav(string text, string wavPath) => throw NotOnLinux.Feature("Text-to-speech (the self-test voice)");
    }
}

namespace AgentEyes
{
    using AgentEyes.Platform.Linux;

    internal static class SelfTest
    {
        public static int Run() => throw NotOnLinux.Feature("The headless self-test (it speaks through Windows TTS)");
    }

    internal static class RegionOverlay
    {
        public static System.Drawing.Rectangle? Select() => Select(RegionMath.AspectLock.Free);

        public static System.Drawing.Rectangle? Select(RegionMath.AspectLock aspect) =>
            throw new UsageException("interactive --region selection is not available on Linux (there is no overlay); "
                + "record a whole --screen, or pass an explicit region through the Control API.");
    }
}
