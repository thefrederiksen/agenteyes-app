using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AgentEyes.AlwaysOn
{
    /// <summary>
    /// Today's always-on counters (issue #66) - "Today: 7 clips kept, 1 h 42 m, 2.1 GB. 6 h 10 m of
    /// silence discarded." Kept on disk beside the pieces so a restart in the middle of the day does
    /// not reset them, and started afresh when the local date changes.
    /// </summary>
    internal sealed class AlwaysOnDay
    {
        public string Date { get; set; } = "";
        public int Clips { get; set; }
        public double KeptSeconds { get; set; }
        public long KeptBytes { get; set; }
        public double DiscardedSeconds { get; set; }

        /// <summary>
        /// The full path of every clip written today, oldest first (issue #70), so the page and the
        /// Control API can say WHERE today's clips are - the clips folder can change during the day.
        /// A counter file from before issue #70 has no list: its clips are counted but not listed.
        /// </summary>
        public List<string> ClipPaths { get; set; } = new();

        public static string Today(DateTime nowUtc) => nowUtc.ToLocalTime().ToString("yyyy-MM-dd");

        /// <summary>Roll over to a fresh day when the date has changed.</summary>
        public void EnsureDay(DateTime nowUtc)
        {
            string today = Today(nowUtc);
            if (Date == today) return;
            Log.Info($"[AlwaysOnDay] EnsureDay: new day {today} (was {(Date.Length == 0 ? "(none)" : Date)}: "
                     + $"{Clips} clips, {KeptSeconds:0}s kept, {DiscardedSeconds:0}s discarded)");
            Date = today;
            Clips = 0;
            KeptSeconds = 0;
            KeptBytes = 0;
            DiscardedSeconds = 0;
            ClipPaths = new List<string>();
        }

        /// <summary>The counters as one line, as the page and the tray show them.</summary>
        public string Summary() =>
            $"{Clips} clip{(Clips == 1 ? "" : "s")} kept, {Duration(KeptSeconds)}, {Size(KeptBytes)}. "
            + $"{Duration(DiscardedSeconds)} of silence discarded.";

        public static string Duration(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} m";
            if (t.TotalMinutes >= 1) return $"{t.Minutes} m";
            return $"{t.Seconds} s";
        }

        public static string Size(long bytes)
        {
            double gb = bytes / 1024.0 / 1024 / 1024;
            if (gb >= 1) return $"{gb:0.0} GB";
            return $"{bytes / 1024.0 / 1024:0} MB";
        }

        public static AlwaysOnDay Load(string path)
        {
            if (!File.Exists(path)) return new AlwaysOnDay();
            try
            {
                var day = JsonSerializer.Deserialize<AlwaysOnDay>(File.ReadAllText(path)) ?? new AlwaysOnDay();
                // An explicit null in the file must not become a null list the keeper then adds to.
                day.ClipPaths ??= new List<string>();
                Log.Info($"[AlwaysOnDay] Load: {path} -> {day.Date}, {day.Clips} clips, {day.ClipPaths.Count} listed");
                return day;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A counter file that cannot be read costs today's totals, never the recorder. Say so.
                Log.Warn($"[AlwaysOnDay] Load: {path} could not be read ({ex.Message}); today's counters start at zero");
                return new AlwaysOnDay();
            }
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
    }
}
