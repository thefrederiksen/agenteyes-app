using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEyes.AlwaysOn
{
    /// <summary>What an always-on history event is about (issue #77).</summary>
    internal enum HistoryKind
    {
        /// <summary>Always-on started, stopped, paused or resumed - and why.</summary>
        State,
        /// <summary>The devices at start: microphone, system sound, Windows mute and volume.</summary>
        Device,
        /// <summary>The once-a-minute level line.</summary>
        Level,
        /// <summary>A piece kept or deleted.</summary>
        Decision,
        /// <summary>A clip written (or removed by the cap).</summary>
        Clip,
        /// <summary>An error, a restart, a recovery, a warning.</summary>
        Problem,
    }

    internal enum HistorySeverity { Info, Warning, Error }

    /// <summary>The History tab's filter (issue #77): All / Decisions / Levels / Problems.</summary>
    internal enum HistoryFilter { All, Decisions, Levels, Problems }

    /// <summary>One line of the always-on history (issue #77): when, what kind, how serious, and a
    /// short plain-English line. <see cref="Detail"/> carries the long part - ffmpeg's last lines -
    /// when there is one.</summary>
    internal sealed class AlwaysOnEvent
    {
        public DateTime AtUtc { get; set; }
        public HistoryKind Kind { get; set; }
        public HistorySeverity Severity { get; set; }
        public string Text { get; set; } = "";
        public string? Detail { get; set; }

        [JsonIgnore]
        public DateTime AtLocal => AtUtc.ToLocalTime();

        /// <summary>
        /// Whether the event is in a filter (issue #77): Decisions are the keep/delete decisions and the
        /// clips; Levels the minute lines; Problems every warning and error, whatever it is about (a
        /// level line flagged for a silent microphone is a problem too).
        /// </summary>
        public bool Matches(HistoryFilter filter) => filter switch
        {
            HistoryFilter.All => true,
            HistoryFilter.Decisions => Kind is HistoryKind.Decision or HistoryKind.Clip,
            HistoryFilter.Levels => Kind == HistoryKind.Level,
            HistoryFilter.Problems => Severity != HistorySeverity.Info,
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "unknown history filter"),
        };
    }

    /// <summary>
    /// The persisted always-on history (issue #77): every decision, level line and problem, one JSON
    /// line per event, in its own file under the always-on work folder. It is what the History tab and
    /// <c>GET /always-on/history</c> read, and it survives an app restart.
    ///
    /// RETENTION. Today and the <see cref="RetentionDays"/> local days before it are kept; older events
    /// are dropped when the file is loaded and again when the day changes. A day of level lines is
    /// ~1440 events, so the file stays in the low megabytes.
    ///
    /// THE HISTORY IS A RECORD OF THE RECORDING, NEVER A CONDITION FOR IT (the rule issue #81 set for
    /// today.json): a file that cannot be written is logged as an error and the event stays in memory;
    /// nothing in the recorder waits on it or stops for it.
    ///
    /// Loaded lazily, on first use, so the app's start-up does not read it on the UI thread; the page
    /// asks for events from a worker. Thread safe: the engine appends on its timer thread while the
    /// page and the Control API read.
    /// </summary>
    internal sealed class AlwaysOnHistory
    {
        /// <summary>How many local days before today are kept.</summary>
        public const int RetentionDays = 7;

        public const string FileName = "history.jsonl";

        private static readonly JsonSerializerOptions LineOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        private readonly object _gate = new();
        private readonly Func<DateTime> _utcNow;
        private List<AlwaysOnEvent>? _events;     // oldest first; null until loaded
        private string _trimmedForDay = "";

        /// <summary>Raised, on the appending thread, for every event appended.</summary>
        public event Action<AlwaysOnEvent>? Appended;

        public AlwaysOnHistory(string path, Func<DateTime>? utcNow = null)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("the history needs a file path", nameof(path));
            Path = path;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>The file the events are kept in.</summary>
        public string Path { get; }

        /// <summary>The oldest moment kept as of <paramref name="nowUtc"/>: the start of the local day
        /// <see cref="RetentionDays"/> days before today's.</summary>
        public static DateTime KeepFromUtc(DateTime nowUtc) =>
            nowUtc.ToLocalTime().Date.AddDays(-RetentionDays).ToUniversalTime();

        /// <summary>The filter for a <c>kind=</c> query value: all (or none) / decisions / levels /
        /// problems. Anything else is refused with the choices.</summary>
        public static HistoryFilter ParseFilter(string? kind)
        {
            if (string.IsNullOrWhiteSpace(kind)) return HistoryFilter.All;
            return kind.Trim().ToLowerInvariant() switch
            {
                "all" => HistoryFilter.All,
                "decisions" => HistoryFilter.Decisions,
                "levels" => HistoryFilter.Levels,
                "problems" => HistoryFilter.Problems,
                _ => throw new UsageException($"kind must be all, decisions, levels or problems, got '{kind}'."),
            };
        }

        /// <summary>One event as its file line. Pure.</summary>
        public static string Serialize(AlwaysOnEvent e) => JsonSerializer.Serialize(e, LineOptions);

        /// <summary>The event on one file line, or null when the line is not one (a torn write, a
        /// hand edit) - the caller logs and skips it.</summary>
        public static AlwaysOnEvent? Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            try
            {
                var e = JsonSerializer.Deserialize<AlwaysOnEvent>(line, LineOptions);
                if (e == null || e.AtUtc == default) return null;
                e.AtUtc = DateTime.SpecifyKind(e.AtUtc, DateTimeKind.Utc);
                return e;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>How many events are kept right now (loads the file when it has not been read yet).</summary>
        public int Count
        {
            get { lock (_gate) return EnsureLoaded(_utcNow()).Count; }
        }

        /// <summary>
        /// Add one event: to memory, to the file, and to every <see cref="Appended"/> listener. Never
        /// throws for the file - see the class remarks.
        /// </summary>
        public void Append(AlwaysOnEvent e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            if (string.IsNullOrWhiteSpace(e.Text)) throw new ArgumentException("an event needs its text", nameof(e));
            if (e.AtUtc == default) e.AtUtc = _utcNow();
            e.AtUtc = DateTime.SpecifyKind(e.AtUtc, DateTimeKind.Utc);
            lock (_gate)
            {
                var events = EnsureLoaded(e.AtUtc);
                events.Add(e);
                // A day change rewrites the whole file with this event already in it; appending it
                // again would double it (caught by Append_OnANewDay_TrimsWhatFellOutOfTheRetention).
                bool rewritten = TrimIfNewDay(events, e.AtUtc);
                if (!rewritten)
                {
                    try
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                        File.AppendAllText(Path, Serialize(e) + Environment.NewLine);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Error($"[AlwaysOnHistory] Append: the event could not be written to {Path}; it is kept in memory "
                                  + "and the recording goes on", ex);
                    }
                }
            }
            try { Appended?.Invoke(e); }
            catch (Exception ex) { Log.Error("[AlwaysOnHistory] Append: a listener failed", ex); }
        }

        /// <summary>The events in a filter, at or after <paramref name="sinceUtc"/> when given, NEWEST
        /// FIRST. Loads the file on the first call.</summary>
        public List<AlwaysOnEvent> Events(DateTime? sinceUtc, HistoryFilter filter)
        {
            lock (_gate)
            {
                var events = EnsureLoaded(_utcNow());
                IEnumerable<AlwaysOnEvent> q = events;
                if (sinceUtc.HasValue)
                {
                    var since = DateTime.SpecifyKind(sinceUtc.Value, DateTimeKind.Utc);
                    q = q.Where(e => e.AtUtc >= since);
                }
                if (filter != HistoryFilter.All) q = q.Where(e => e.Matches(filter));
                var list = q.ToList();
                list.Reverse();
                return list;
            }
        }

        /// <summary>Read the file once. Caller holds the lock.</summary>
        private List<AlwaysOnEvent> EnsureLoaded(DateTime nowUtc)
        {
            if (_events != null) return _events;
            var events = new List<AlwaysOnEvent>();
            int bad = 0;
            if (File.Exists(Path))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(Path))
                    {
                        if (line.Length == 0) continue;
                        var e = Parse(line);
                        if (e == null) { bad++; continue; }
                        events.Add(e);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A history that cannot be read costs the old events, never the recorder. Say so.
                    Log.Error($"[AlwaysOnHistory] Load: {Path} could not be read; the history starts empty", ex);
                }
            }
            events.Sort((a, b) => a.AtUtc.CompareTo(b.AtUtc));
            _events = events;
            int before = events.Count;
            DateTime keepFrom = KeepFromUtc(nowUtc);
            events.RemoveAll(e => e.AtUtc < keepFrom);
            _trimmedForDay = LocalDay(nowUtc);
            if (before != events.Count || bad > 0) Rewrite(events);
            Log.Info($"[AlwaysOnHistory] Load: {Path} -> {events.Count} events kept"
                     + (before != events.Count ? $", {before - events.Count} older than {keepFrom.ToLocalTime():yyyy-MM-dd} dropped" : "")
                     + (bad > 0 ? $", {bad} unreadable line(s) skipped" : ""));
            return events;
        }

        /// <summary>Once per local day: drop what fell out of the retention and rewrite the file. Caller holds the lock.</summary>
        /// <returns>True when the file was rewritten (it then already holds every event in the list).</returns>
        private bool TrimIfNewDay(List<AlwaysOnEvent> events, DateTime nowUtc)
        {
            string day = LocalDay(nowUtc);
            if (day == _trimmedForDay) return false;
            _trimmedForDay = day;
            DateTime keepFrom = KeepFromUtc(nowUtc);
            int dropped = events.RemoveAll(e => e.AtUtc < keepFrom);
            if (dropped == 0) return false;
            Log.Info($"[AlwaysOnHistory] Trim: new day {day} - {dropped} events older than {keepFrom.ToLocalTime():yyyy-MM-dd} dropped");
            Rewrite(events);
            return true;
        }

        private void Rewrite(List<AlwaysOnEvent> events)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                string tmp = Path + ".tmp";
                File.WriteAllLines(tmp, events.Select(Serialize));
                File.Move(tmp, Path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"[AlwaysOnHistory] Rewrite: {Path} could not be rewritten; the old lines stay until the next trim", ex);
            }
        }

        private static string LocalDay(DateTime nowUtc) =>
            nowUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
