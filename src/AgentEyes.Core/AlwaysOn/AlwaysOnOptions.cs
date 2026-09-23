using System;
using System.IO;
using Drawing = System.Drawing;

namespace AgentEyes.AlwaysOn
{
    /// <summary>
    /// Everything one always-on run needs (issue #66), resolved by the app from the chosen recording
    /// setup and the Always On page. Immutable for the run; a changed setting takes effect on the next
    /// start.
    /// </summary>
    internal sealed class AlwaysOnOptions
    {
        /// <summary>The preset's name, for the status line and the log.</summary>
        public string SetupName { get; init; } = "";

        /// <summary>The screen region recorded, in virtual-desktop device pixels.</summary>
        public Drawing.Rectangle Capture { get; init; }

        /// <summary>The virtual-desktop bounds, to clamp and pad an oversized region.</summary>
        public Drawing.Rectangle? Desktop { get; init; }

        /// <summary>The microphone recorded into the pieces (DirectShow name), or null for none.</summary>
        public string? DshowMic { get; init; }

        /// <summary>The microphone whose LEVEL is measured when its sound counts (a WaveIn name
        /// fragment). Separate from <see cref="DshowMic"/>: the mic can count without being recorded.</summary>
        public string? MicLevelDevice { get; init; }

        /// <summary>True when the system sound is recorded into the pieces.</summary>
        public bool RecordSystem { get; init; }

        public double MicGain { get; init; } = 1.0;
        public double SystemGain { get; init; } = 0.7;

        /// <summary>Which sound decides what is kept.</summary>
        public SoundSource Counts { get; init; } = SoundSource.Mic;

        /// <summary>The fixed line in dBFS, or null for Auto.</summary>
        public double? ThresholdDb { get; init; }

        public TimeSpan KeepBefore { get; init; } = TimeSpan.FromMinutes(5);
        public TimeSpan KeepAfter { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>The most disk the clips may use, in bytes. Zero means no cap.</summary>
        public long CapBytes { get; init; } = 5L * 1024 * 1024 * 1024;

        /// <summary>Where finished clips are written, one MP4 each.</summary>
        public string ClipsFolder { get; init; } = DefaultClipsFolder;

        /// <summary>Where the pieces and the per-clip holding folders live while they are decided.</summary>
        public string WorkFolder { get; init; } = DefaultWorkFolder;

        /// <summary>Always-on frame rate. Lower than a normal recording's, measured lighter (see
        /// <see cref="AlwaysOnArgs.EncoderPreference"/>).</summary>
        public int Fps { get; init; } = 10;

        /// <summary>The length of one piece. Sixty seconds in the product; tests shorten it.</summary>
        public int PieceSeconds { get; init; } = 60;

        public string PieceFolder => Path.Combine(WorkFolder, "pieces");
        public string PendingFolder => Path.Combine(WorkFolder, "pending");
        public string StatsFile => Path.Combine(WorkFolder, "today.json");

        /// <summary>The clips this engine wrote, one file name per line - the only files the cap may delete.</summary>
        public string ClipLedger => Path.Combine(WorkFolder, "clips.txt");

        /// <summary>Kept pieces that could not be read, set aside rather than deleted.</summary>
        public string UnreadableFolder => Path.Combine(WorkFolder, "unreadable");

        public static string DefaultClipsFolder => Path.Combine(RecordingPaths.Root, "AlwaysOn");

        public static string DefaultWorkFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentEyes", "alwayson");

        public override string ToString() =>
            $"setup=\"{SetupName}\" capture={Capture} mic={(DshowMic ?? "(none)")} system={RecordSystem} "
            + $"counts={Counts} threshold={(ThresholdDb.HasValue ? ThresholdDb.Value.ToString("0.#") + " dBFS" : "auto")} "
            + $"before={KeepBefore.TotalMinutes:0.##}m after={KeepAfter.TotalMinutes:0.##}m cap={CapBytes / 1024.0 / 1024 / 1024:0.##}GB "
            + $"fps={Fps} piece={PieceSeconds}s clips={ClipsFolder}";
    }
}
