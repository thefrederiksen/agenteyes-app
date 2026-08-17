using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentEyes;
using AgentEyes.Audio;

namespace AgentEyes.App
{
    /// <summary>
    /// A named, saved bundle of every capture setting - the OBS-style "profile". The launcher picks one
    /// of these and records; the editor is the only place the individual fields are exposed.
    /// </summary>
    internal sealed class CapturePreset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Untitled";
        public string? Note { get; set; }

        public int MonitorIndex { get; set; } = 1;
        public bool UseRegion { get; set; }
        public int[]? Region { get; set; }            // [x, y, w, h] in device pixels

        // Issue #124: mic-only is the trustworthy default. The mic captures room + speaker audio
        // correctly timed; the separate system-loopback remix collapses its timeline (music lands
        // at 0:00 - issue #126) so "mixed" is opt-in until that is fixed.
        public string Source { get; set; } = "mic"; // mic | system | mixed
        public string? Mic { get; set; }              // microphone name fragment
        public bool Denoise { get; set; } = true;     // RNNoise noise suppression on the mic
        // Issue #83: null = follow the source default (OFF for mic-only, ON for mixed/system - see
        // GateDefaults). A concrete true/false is the user's explicit choice from the preset editor.
        public bool? Gate { get; set; }
        public bool Level { get; set; } = true;       // voice leveling (speechnorm) on the mic
        public double MicVol { get; set; } = 100;     // percent
        public double SysVol { get; set; } = 70;      // percent

        public string Mode { get; set; } = "video";   // shot | audio | video
        public int Fps { get; set; } = 30;

        // List containers expose ToString as their UI Automation name - return the preset name so
        // the launcher combo is readable to accessibility tools and drivable by the GUI smoke test.
        public override string ToString() => Name;

        public CapturePreset Clone() => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Name,
            Note = Note,
            MonitorIndex = MonitorIndex,
            UseRegion = UseRegion,
            Region = Region?.ToArray(),
            Source = Source,
            Mic = Mic,
            Denoise = Denoise,
            Gate = Gate,
            Level = Level,
            MicVol = MicVol,
            SysVol = SysVol,
            Mode = Mode,
            Fps = Fps,
        };

        /// <summary>One-glance summary shown under the launcher's preset picker.</summary>
        public string Summary()
        {
            string screen = UseRegion && Region is { Length: 4 }
                ? $"Monitor {MonitorIndex} region {Region[2]}x{Region[3]}"
                : $"Monitor {MonitorIndex}";
            string mode = Mode switch { "shot" => "Screenshot", "audio" => "Audio + shots", _ => $"Video {Fps}fps" };
            if (Mode == "shot") return $"{screen}\n{mode}";

            string src = Source switch { "mic" => "Mic only", "system" => "System only", _ => "Mic + System (mixed)" };
            string mic = Source == "system" ? "(system loopback)" : (string.IsNullOrWhiteSpace(Mic) ? DefaultMicDisplay() : Mic!);
            var fx = new List<string>();
            if (Denoise) fx.Add("denoise");
            if (Gate ?? GateDefaults.For(Source)) fx.Add("gate");
            if (Level) fx.Add("level");
            string mix = $"{(fx.Count > 0 ? string.Join("+", fx) : "no fx")} - mic {MicVol:F0}% / sys {SysVol:F0}%";
            return $"{screen}\n{mode} - {src}\n{mic} - {mix}";
        }

        /// <summary>Summary line for Mic = null: name what the default currently resolves to.
        /// Display-only - record-time resolution still fails loudly if Windows has no default.</summary>
        private static string DefaultMicDisplay()
        {
            try { return $"Default mic ({DefaultMic.FriendlyName()})"; }
            catch { return "Default mic (none set in Windows!)"; }
        }
    }

    /// <summary>Loads/saves presets to %LOCALAPPDATA%\AgentEyes\presets.json and seeds a "Default" on first run.</summary>
    internal static class PresetStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentEyes", "presets.json");
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static List<CapturePreset> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var list = JsonSerializer.Deserialize<List<CapturePreset>>(File.ReadAllText(FilePath));
                    if (list is { Count: > 0 }) return list;
                }
            }
            catch (Exception ex) { Log.Error("presets load", ex); }

            var seeded = new List<CapturePreset> { Default() };
            Save(seeded);
            return seeded;
        }

        public static void Save(List<CapturePreset> presets)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(presets, JsonOpts));
            }
            catch (Exception ex) { Log.Error("presets save", ex); }
        }

        /// <summary>The out-of-the-box preset: primary monitor, video, mic-only audio (issue #124).</summary>
        public static CapturePreset Default()
        {
            int primary = Monitors.All().FirstOrDefault(m => m.Primary)?.Index ?? 1;
            string? firstMic = AudioCapture.Devices().Length > 0 ? AudioCapture.Devices()[0].Name : null;
            return new CapturePreset
            {
                Name = "Default",
                MonitorIndex = primary,
                Source = "mic",
                Mic = firstMic,
                Denoise = true,
                Gate = null,   // follow the source default (off for mic-only)
                Level = true,
                MicVol = 100,
                SysVol = 70,
                Mode = "video",
                Fps = 30,
            };
        }
    }

    /// <summary>Maps a preset onto the shared RecordingService. Used by the launcher, the tray and the REST API.</summary>
    internal static class PresetCapture
    {
        /// <summary>Starts the recording (or takes the screenshot) described by the preset. Returns the
        /// screenshot path for "shot" mode, otherwise null (a recording is now in progress).</summary>
        public static string? Start(RecordingService svc, CapturePreset p)
        {
            int screen = p.MonitorIndex;
            int[]? region = p.UseRegion ? p.Region : null;
            var src = RecordingService.ParseSource(p.Source);
            var opts = new AudioMixOptions
            {
                NoiseSuppression = p.Denoise,
                // Issue #83: null = the source default (mic-only OFF, mixed/system ON); a concrete
                // value is the user's explicit editor choice and is honored as-is.
                NoiseGate = p.Gate ?? GateDefaults.For(src),
                VoiceLeveling = p.Level,
                MicGain = p.MicVol / 100.0,
                SystemGain = p.SysVol / 100.0,
            };

            // Mic = null means "system default microphone": resolve it to a concrete device
            // name now, at record time. Throws a clear error if Windows has none. The full
            // WASAPI name matches both resolver lists (WaveIn carries full names since #9,
            // DirectShow always did).
            string? mic = p.Mic;
            if (p.Mode != "shot" && string.IsNullOrWhiteSpace(mic)
                && src is AudioSourceKind.Mic or AudioSourceKind.Mixed)
            {
                mic = DefaultMic.FriendlyName();
            }

            switch (p.Mode)
            {
                case "shot": return svc.Screenshot(screen, region);
                case "audio": svc.StartAudio(screen, src, mic, opts); return null;
                default: svc.StartVideo(screen, src, mic, region, opts, p.Fps); return null;
            }
        }
    }
}
