using System;
using System.Globalization;
using System.Threading;
using AgentEyes.Audio;
using AgentEyes.Video;

namespace AgentEyes.AlwaysOn
{
    /// <summary>
    /// <c>agenteyes always-on</c> (issue #66): run always-on in the foreground from the command line,
    /// printing the state as it goes. The app is how a person uses always-on; this is how an agent
    /// measures it and proves the keep rule without a window - it drives the same engine.
    /// </summary>
    internal static class AlwaysOnCli
    {
        public const string Usage =
            "  agenteyes always-on --screen N [--mic \"NAME\"] [--system] [--counts mic|system|both] [--threshold DB|auto]\n"
            + "                     [--before MIN] [--after MIN] [--cap-gb N] [--piece SECONDS] [--fps N] [--seconds N]\n"
            + "                     [--clips DIR] [--work DIR]    record all day, keep only the stretches with sound";

        public static int Run(CliArgs opts)
        {
            var inv = CultureInfo.InvariantCulture;
            int screen = opts.Has("screen") ? opts.RequireInt("screen", "e.g. --screen 1") : 1;
            var mon = Monitors.Require(screen);

            string? micFragment = opts.Get("mic");
            string? dshowMic = micFragment == null ? null : DeviceResolver.ResolveName(FfmpegDevices.ListAudio(), micFragment);
            bool system = opts.Has("system");
            var counts = ParseCounts(opts.Get("counts") ?? (micFragment != null ? "mic" : "system"));
            double? threshold = ParseThreshold(opts.Get("threshold"));
            double before = ParseDouble(opts.Get("before"), 5, "before");
            double after = ParseDouble(opts.Get("after"), 5, "after");
            double capGb = ParseDouble(opts.Get("cap-gb"), 5, "cap-gb");
            int piece = opts.Has("piece") ? opts.RequireInt("piece", "e.g. --piece 60") : 60;
            int fps = opts.Has("fps") ? opts.RequireInt("fps", "e.g. --fps 10") : 10;
            int seconds = opts.Has("seconds") ? opts.RequireInt("seconds", "e.g. --seconds 600") : 0;

            string? micLevel = micFragment;
            if (counts is SoundSource.Mic or SoundSource.Both && micLevel == null) micLevel = DefaultMic.FriendlyName();

            var o = new AlwaysOnOptions
            {
                SetupName = "cli",
                Capture = mon.Bounds,
                Desktop = Monitors.VirtualBounds(),
                DshowMic = dshowMic,
                MicLevelDevice = micLevel,
                RecordSystem = system,
                Counts = counts,
                ThresholdDb = threshold,
                KeepBefore = TimeSpan.FromMinutes(before),
                KeepAfter = TimeSpan.FromMinutes(after),
                CapBytes = (long)(capGb * 1024 * 1024 * 1024),
                ClipsFolder = opts.Get("clips") ?? AlwaysOnOptions.DefaultClipsFolder,
                WorkFolder = opts.Get("work") ?? AlwaysOnOptions.DefaultWorkFolder + "-cli",
                Fps = fps,
                PieceSeconds = piece,
            };

            using var engine = new AlwaysOnEngine();
            engine.Start(o);
            Console.WriteLine($"[ok] always-on started: {o}");
            Console.WriteLine(seconds > 0 ? $"     running for {seconds}s" : "     press Q to stop");

            var until = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
            var nextPrint = DateTime.UtcNow;
            while (DateTime.UtcNow < until)
            {
                if (seconds == 0 && Console.KeyAvailable && char.ToUpperInvariant(Console.ReadKey(true).KeyChar) == 'Q') break;
                if (DateTime.UtcNow >= nextPrint)
                {
                    Print(engine.Status());
                    nextPrint = DateTime.UtcNow.AddSeconds(15);
                }
                Thread.Sleep(250);
            }

            engine.Stop();
            var s = engine.Status();
            Console.WriteLine($"[ok] always-on stopped. Today: {engine.TodaySummary()}");
            if (s.LastClip != null) Console.WriteLine($"     last clip: {s.LastClip}");
            return 0;
        }

        private static void Print(AlwaysOnStatus s)
        {
            string last = s.LastSoundUtc.HasValue ? s.LastSoundUtc.Value.ToLocalTime().ToString("HH:mm:ss") : "never";
            string line = s.ThresholdDb.HasValue ? $"{s.ThresholdDb.Value:0.0} dBFS{(s.ThresholdAuto ? " (auto)" : "")}" : "measuring";
            string floor = s.FloorDb.HasValue ? $"{s.FloorDb.Value:0.0} dBFS" : "measuring";
            Console.WriteLine($"  {DateTime.Now:HH:mm:ss} state={s.State} encoder={s.Encoder} line={line} floor={floor} lastSound={last} "
                              + $"waiting={s.PiecesWaiting} openClipPieces={s.PiecesKeptInOpenClip} clips={s.ClipsToday} "
                              + $"kept={s.KeptSecondsToday:0}s discarded={s.DiscardedSecondsToday:0}s"
                              + (s.LastError != null ? $" error=\"{s.LastError}\"" : ""));
        }

        internal static SoundSource ParseCounts(string s) => s.ToLowerInvariant() switch
        {
            "mic" or "microphone" => SoundSource.Mic,
            "system" => SoundSource.System,
            "both" => SoundSource.Both,
            _ => throw new UsageException($"--counts must be mic, system or both, got '{s}'."),
        };

        internal static double? ParseThreshold(string? s)
        {
            if (s == null || s.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d <= 0) return d;
            throw new UsageException($"--threshold must be auto or a dBFS value at or below 0 (e.g. -40), got '{s}'.");
        }

        private static double ParseDouble(string? s, double def, string key)
        {
            if (s == null) return def;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d >= 0) return d;
            throw new UsageException($"--{key} must be a number at or above 0, got '{s}'.");
        }
    }
}
