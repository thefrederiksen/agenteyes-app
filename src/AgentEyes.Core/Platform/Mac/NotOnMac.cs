using System;
using System.Diagnostics;

namespace AgentEyes.Platform.Mac
{
    /// <summary>
    /// The one wording for a Windows-only feature reached on macOS. These features are OUT of the
    /// macOS scope (always-on, the self-test's text-to-speech voice, tones, the interactive region
    /// overlay, mute-state reads) - they fail loudly with this, never silently do nothing. The twin
    /// of NotOnLinux, and the same scope: the Mac build is the headless engine, not the tray app.
    /// </summary>
    internal static class NotOnMac
    {
        public static PlatformNotSupportedException Feature(string feature)
        {
            string msg = $"{feature} is not available on macOS (the macOS build is the headless "
                + "recorder and its Control API; this part of AgentEyes is still Windows-only).";
            Log.Error("[NotOnMac] " + msg);
            return new PlatformNotSupportedException(msg);
        }
    }
}

namespace AgentEyes.Audio
{
    using AgentEyes.Platform.Mac;

    /// <summary>Windows' endpoint mute/volume for a microphone. macOS: not read (always-on only).</summary>
    internal readonly record struct MicEndpointState(string Name, bool Muted, double VolumePercent)
    {
        public string Describe() => $"{Name}: {(Muted ? "MUTED" : "not muted")}, volume {VolumePercent:0}%";
    }

    internal static class MicEndpoint
    {
        public static MicEndpointState Read(string? nameFragment) => throw NotOnMac.Feature("Reading the microphone mute state");
    }

    internal static class DefaultMic
    {
        public static string FriendlyName() => throw NotOnMac.Feature("Picking the default microphone for always-on");
    }

    internal sealed class TonePlayer : IDisposable
    {
        public void Play(string wavPath) => throw NotOnMac.Feature("Playing test tones");
        public void Stop() { }
        public void Dispose() { }
    }
}

namespace AgentEyes.AlwaysOn
{
    using AgentEyes.Platform.Mac;

    internal static class KillOnCloseJob
    {
        public static void Assign(Process process) => throw NotOnMac.Feature("Always-on recording");
    }

    /// <summary>Always-on's piece recorder. macOS: always-on is out of scope, so it never starts.</summary>
    internal sealed class ContinuousRecorder : IDisposable
    {
        public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(10);
        public string CommandLine => "";
        public string Encoder => "";
        public DateTime StartedUtc => default;
        public bool HasExited => false;
        public string StderrTail => "";
        public string ProcessState => "not started";
        public static string ChooseEncoder() => throw NotOnMac.Feature("Always-on recording");
        public void Start(AlwaysOnOptions o, SoundLog sound) => throw NotOnMac.Feature("Always-on recording");
        public void Stop() { }
        public void Dispose() { }
    }
}

namespace AgentEyes.Packaging
{
    using AgentEyes.Platform.Mac;

    internal static class SpeechGen
    {
        public static void ToWav(string text, string wavPath) => throw NotOnMac.Feature("Text-to-speech (the self-test voice)");
    }
}

namespace AgentEyes
{
    using AgentEyes.Platform.Mac;

    internal static class SelfTest
    {
        public static int Run() => throw NotOnMac.Feature("The headless self-test (it speaks through Windows text-to-speech)");
    }

    internal static class RegionOverlay
    {
        public static System.Drawing.Rectangle? Select() => Select(RegionMath.AspectLock.Free);

        public static System.Drawing.Rectangle? Select(RegionMath.AspectLock aspect) =>
            throw new UsageException("interactive --region selection is not available on macOS (there is no overlay); "
                + "record a whole --screen, or pass an explicit region through the Control API.");
    }
}
