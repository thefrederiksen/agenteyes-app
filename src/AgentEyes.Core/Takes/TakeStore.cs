using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEyes.Takes
{
    /// <summary>One recorded attempt at a take (issue #230). Every attempt is kept.</summary>
    internal sealed class TakeAttempt
    {
        public int Number { get; set; }
        public string File { get; set; } = "";
        public string Dir { get; set; } = "";
        public double DurationSeconds { get; set; }
        public DateTime RecordedUtc { get; set; }

        /// <summary>The continuous recording this attempt was cut from (issue #230 round 4, review I5),
        /// so a video session can reach everything said when a cut is not what it wanted.</summary>
        public string? SessionFile { get; set; }
    }

    /// <summary>One line to say to camera (issue #230).</summary>
    internal sealed class Take
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public string Text { get; set; } = "";
        public double? TargetSeconds { get; set; }
        public string? Instructions { get; set; }
        public string? Background { get; set; }
        public List<TakeAttempt> Attempts { get; set; } = new();

        /// <summary>The attempt marked to use, or null when none is chosen.</summary>
        public int? Chosen { get; set; }
    }

    /// <summary>
    /// What a take attempt's recording is called (issue #230 round 2): named by its take -
    /// "Take 1 - The opening - attempt 2", or "Take 1 - attempt 2" when the take has no title -
    /// and marked with the take id so the library never gives it an AI title or description.
    /// </summary>
    internal sealed record TakeName(string TakeId, string DisplayName)
    {
        public static TakeName For(int takeNumber, Take take, int attempt)
        {
            if (take == null) throw new ArgumentNullException(nameof(take));
            string name = string.IsNullOrWhiteSpace(take.Title)
                ? $"Take {takeNumber} - attempt {attempt}"
                : $"Take {takeNumber} - {take.Title.Trim()} - attempt {attempt}";
            return new TakeName(take.Id, name);
        }
    }

    /// <summary>The list a video session schedules (issue #230), with the devices to record with.</summary>
    internal sealed class TakeList
    {
        public string? Title { get; set; }
        public string? Camera { get; set; }
        public string? Mic { get; set; }

        /// <summary>"teleprompter" (the default) or "read-first".</summary>
        public string? ReadingMode { get; set; }

        /// <summary>1-based monitor to record on, or null for the default (the first non-primary).</summary>
        public int? Screen { get; set; }

        public List<Take> Takes { get; set; } = new();
    }

    /// <summary>
    /// The take list on disk (issue #230): <c>%LOCALAPPDATA%\AgentEyes\takes\takes.json</c>. One list
    /// at a time; loading a new list keeps the attempts of takes whose id is unchanged, so a video
    /// session can re-send its list without losing what was recorded. Thread-safe.
    /// </summary>
    internal sealed class TakeStore
    {
        public const string ReadFirst = "read-first";
        public const string Teleprompter = "teleprompter";

        /// <summary>The reading mode when a list does not say (issue #230 round 2): the teleprompter,
        /// because read-first hides the text and that surprised the first live take.</summary>
        public const string DefaultReadingMode = Teleprompter;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly object _lock = new();
        private readonly string _path;
        private TakeList _list;

        /// <summary>Raised (on the calling thread) after any change.</summary>
        public event Action? Changed;

        public static string DefaultPath => Path.Combine(AppDataPaths.Root, "takes", "takes.json");

        private static TakeStore? _shared;
        private static readonly object SharedLock = new();

        /// <summary>The one store the app's windows and the Control API share.</summary>
        public static TakeStore Shared
        {
            get { lock (SharedLock) return _shared ??= new TakeStore(DefaultPath); }
        }

        public TakeStore(string path)
        {
            _path = path;
            _list = File.Exists(path) ? Read(path) : new TakeList();
            Log.Info($"[TakeStore] opened {path}: {_list.Takes.Count} take(s)");
        }

        private static TakeList Read(string path)
        {
            var list = JsonSerializer.Deserialize<TakeList>(File.ReadAllText(path), JsonOpts);
            if (list == null) throw new InvalidDataException($"the take list {path} is empty or not JSON.");
            return list;
        }

        /// <summary>A deep copy of the current list, safe to read on any thread.</summary>
        public TakeList Snapshot()
        {
            lock (_lock) return Clone(_list);
        }

        /// <summary>
        /// Replace the list. Each take needs an id and text; ids must be unique. Attempts and the
        /// chosen mark of a take whose id was already in the list are carried over. Devices,
        /// reading mode and screen not given in <paramref name="incoming"/> keep their current values.
        /// </summary>
        public TakeList Load(TakeList incoming)
        {
            if (incoming == null) throw new UsageException("a take list is required.");
            if (incoming.Takes == null || incoming.Takes.Count == 0)
                throw new UsageException("a take list needs at least one take.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < incoming.Takes.Count; i++)
            {
                var t = incoming.Takes[i];
                if (string.IsNullOrWhiteSpace(t.Id)) throw new UsageException($"take {i + 1} has no id.");
                if (string.IsNullOrWhiteSpace(t.Text)) throw new UsageException($"take '{t.Id}' has no text.");
                if (!seen.Add(t.Id)) throw new UsageException($"take id '{t.Id}' is used twice.");
            }
            ValidateReadingMode(incoming.ReadingMode);

            lock (_lock)
            {
                var old = _list.Takes.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
                var next = new TakeList
                {
                    Title = incoming.Title,
                    Camera = incoming.Camera ?? _list.Camera,
                    Mic = incoming.Mic ?? _list.Mic,
                    ReadingMode = incoming.ReadingMode ?? _list.ReadingMode ?? DefaultReadingMode,
                    Screen = incoming.Screen ?? _list.Screen,
                };
                foreach (var t in incoming.Takes)
                {
                    var copy = new Take
                    {
                        Id = t.Id.Trim(),
                        Title = t.Title,
                        Text = t.Text.Trim(),
                        TargetSeconds = t.TargetSeconds,
                        Instructions = t.Instructions,
                        Background = t.Background,
                    };
                    if (old.TryGetValue(copy.Id, out var before))
                    {
                        copy.Attempts = before.Attempts;
                        copy.Chosen = before.Chosen;
                    }
                    next.Takes.Add(copy);
                }
                _list = next;
                SaveLocked();
                Log.Info($"[TakeStore] Load: {next.Takes.Count} take(s), title={next.Title ?? "(none)"}");
            }
            Changed?.Invoke();
            return Snapshot();
        }

        /// <summary>
        /// Record a finished attempt against its take; returns it. <paramref name="number"/> is the
        /// number the recording screen gave the attempt when its start was marked (issue #230 round
        /// 3: attempts are cut afterwards, so the number is fixed before the file exists); null
        /// takes the next free number. A number already used is refused.
        /// </summary>
        public TakeAttempt AddAttempt(string takeId, string file, string dir, double durationSeconds, int? number = null,
            string? sessionFile = null)
        {
            TakeAttempt attempt;
            lock (_lock)
            {
                var take = RequireLocked(takeId);
                if (number is int n && (n < 1 || take.Attempts.Any(a => a.Number == n)))
                    throw new UsageException($"take '{takeId}' cannot get attempt number {n}: it is taken or not positive.");
                attempt = new TakeAttempt
                {
                    Number = number ?? NextAttemptNumber(take),
                    File = file,
                    Dir = dir,
                    DurationSeconds = Math.Round(durationSeconds, 2),
                    RecordedUtc = DateTime.UtcNow,
                    SessionFile = sessionFile,
                };
                take.Attempts.Add(attempt);
                SaveLocked();
                Log.Info($"[TakeStore] AddAttempt: take={takeId} attempt={attempt.Number} file={file}");
            }
            Changed?.Invoke();
            return attempt;
        }

        /// <summary>The number the next attempt at this take will get.</summary>
        public int NextAttemptNumber(string takeId)
        {
            lock (_lock) return NextAttemptNumber(RequireLocked(takeId));
        }

        private static int NextAttemptNumber(Take take) =>
            take.Attempts.Count == 0 ? 1 : take.Attempts.Max(a => a.Number) + 1;

        /// <summary>The take's 1-based position in the list - its "Take N".</summary>
        public int TakeNumber(string takeId)
        {
            lock (_lock)
            {
                var take = RequireLocked(takeId);
                return _list.Takes.IndexOf(take) + 1;
            }
        }

        /// <summary>Mark an attempt as the one to use; null clears the mark.</summary>
        public void Choose(string takeId, int? attemptNumber)
        {
            lock (_lock)
            {
                var take = RequireLocked(takeId);
                if (attemptNumber is int n && take.Attempts.All(a => a.Number != n))
                    throw new UsageException($"take '{takeId}' has no attempt {n}.");
                take.Chosen = attemptNumber;
                SaveLocked();
                Log.Info($"[TakeStore] Choose: take={takeId} attempt={attemptNumber?.ToString() ?? "(none)"}");
            }
            Changed?.Invoke();
        }

        /// <summary>Remember the set-up choices (devices, reading mode, screen).</summary>
        public void SaveSettings(string? camera, string? mic, string readingMode, int? screen)
        {
            ValidateReadingMode(readingMode);
            lock (_lock)
            {
                _list.Camera = camera;
                _list.Mic = mic;
                _list.ReadingMode = readingMode;
                _list.Screen = screen;
                SaveLocked();
                Log.Info($"[TakeStore] SaveSettings: camera={camera ?? "(none)"} mic={mic ?? "(none)"} mode={readingMode} screen={screen?.ToString() ?? "(default)"}");
            }
        }

        private static void ValidateReadingMode(string? mode)
        {
            if (mode != null && mode != ReadFirst && mode != Teleprompter)
                throw new UsageException($"readingMode must be '{ReadFirst}' or '{Teleprompter}', got '{mode}'.");
        }

        private Take RequireLocked(string takeId) =>
            _list.Takes.FirstOrDefault(t => t.Id.Equals(takeId, StringComparison.OrdinalIgnoreCase))
            ?? throw new UsageException($"no take with id '{takeId}'.");

        private void SaveLocked()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_list, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }

        private static TakeList Clone(TakeList list) =>
            JsonSerializer.Deserialize<TakeList>(JsonSerializer.Serialize(list, JsonOpts), JsonOpts)!;

        /// <summary>Read a take list from a JSON body or file.</summary>
        public static TakeList Parse(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<TakeList>(json, JsonOpts)
                       ?? throw new UsageException("the take list is empty.");
            }
            catch (JsonException ex)
            {
                throw new UsageException("the take list is not valid JSON: " + ex.Message);
            }
        }

        public static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOpts);
    }
}
